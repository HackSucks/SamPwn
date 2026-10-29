#!/usr/bin/env bash
# SamPwn uninstaller.
#
# Restores the original /opt/etc/parseinfo.sh, removes the SamPwn daemon and
# transient systemd units, and uninstalls the widget.  The root shell is
# stopped last so this script can finish talking to the TV.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
TV_IP=${TV_IP:-}
HOST_IP=${HOST_IP:-}
PYTHON=${PYTHON:-python3}
KEEP_DATA=0
KEEP_WIDGET=0
APP_ID=sampwnroot.dashboard
PACKAGE=sampwnroot

usage() {
    cat <<'EOF'
Usage: ./uninstall.sh --tv TV_IP [options]

  --tv IP          TV IPv4 address (required unless TV_IP is set)
  --host IP        laptop IPv4 reachable from the TV (auto-detected)
  --keep-data      keep /opt/etc/sampwn (shell state, logs, daemon files)
  --keep-widget    leave the SamPwn widget installed
  -h, --help       show this help

Environment: TV_IP, HOST_IP, PYTHON, TIZEN_STUDIO
EOF
}

info() { printf '[sampwn] %s\n' "$*"; }
warn() { printf '[sampwn] WARNING: %s\n' "$*" >&2; }
die()  { printf '[sampwn] ERROR: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --tv) [ $# -ge 2 ] || die "--tv needs an argument"; TV_IP=$2; shift 2 ;;
        --host) [ $# -ge 2 ] || die "--host needs an argument"; HOST_IP=$2; shift 2 ;;
        --keep-data) KEEP_DATA=1; shift ;;
        --keep-widget) KEEP_WIDGET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

[ -n "$TV_IP" ] || die "TV address is required: use --tv IP (or set TV_IP)"
[[ "$TV_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "invalid TV IPv4: $TV_IP"

command -v openssl >/dev/null || die "openssl is required"
[ -x "$PYTHON" ] || command -v "$PYTHON" >/dev/null || die "python3 not found"
"$PYTHON" - <<'PY' || die "Python 3.12 or newer is required"
import sys
raise SystemExit(0 if sys.version_info >= (3, 12) else 1)
PY

if [ -z "$HOST_IP" ]; then
    HOST_IP=$(ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}')
fi
if [ -z "$HOST_IP" ]; then
    HOST_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
fi
[ -n "$HOST_IP" ] || die "could not auto-detect the laptop address; use --host IP"

for dir in ${TIZEN_STUDIO:+"$TIZEN_STUDIO"} "$HOME/tizen-studio" /opt/tizen-studio /usr/local/tizen-studio; do
    if [ -d "$dir/tools" ]; then
        PATH="$dir/tools/ide/bin:$dir/tools:$PATH"
        export TIZEN_STUDIO=$dir
        break
    fi
done
export PATH

REMOTE_UNINSTALL=$(cat <<EOF
set +e
echo SAMPWN_UNINSTALL_START

if [ -f /opt/etc/parseinfo.orig.sh ]; then
    cp -p /opt/etc/parseinfo.orig.sh /opt/etc/parseinfo.sh || { echo 'SAMPWN_UNINSTALL_FAIL restore hook'; exit 40; }
    echo 'original parseinfo.sh restored'
elif [ -f /opt/etc/parseinfo.sh ] && grep -q 'SamPwn boot hook' /opt/etc/parseinfo.sh; then
    rm -f /opt/etc/parseinfo.sh
    echo 'WARNING: no parseinfo.orig.sh backup; removed the SamPwn hook'
else
    echo 'no SamPwn hook found'
fi

/bin/systemctl stop --no-block sampwn-ui.service >/dev/null 2>&1
/bin/rm -f /run/systemd/system/sampwn.service /run/systemd/system/sampwn-ui.service
/bin/systemctl daemon-reload >/dev/null 2>&1
EOF
)

if [ "$KEEP_DATA" = 0 ]; then
    REMOTE_UNINSTALL+=$'\nrm -rf /opt/etc/sampwn\n'
else
    REMOTE_UNINSTALL+=$'\n# keeping /opt/etc/sampwn (--keep-data)\n'
fi

REMOTE_UNINSTALL+=$(cat <<'EOF'
# Stop the daemon last: when this command arrived through the live root shell,
# the shell is a child of the daemon and dies with it.
( sleep 2; /bin/systemctl stop --no-block sampwn.service >/dev/null 2>&1; /usr/bin/pkill -f SampwnSrv.dll >/dev/null 2>&1 ) >/dev/null 2>&1 &
echo SAMPWN_UNINSTALL_OK
exit 0
EOF
)

root_shell_ok() {
    "$PYTHON" "$ROOT/tools/rootshell.py" --host "$TV_IP" --port 2323 --quiet \
        'id' >/dev/null 2>&1
}

info "removing SamPwn from $TV_IP"
set +e
if root_shell_ok; then
    info "reusing the live root shell"
    OUT=$("$PYTHON" "$ROOT/tools/rootshell.py" --host "$TV_IP" --port 2323 \
        "$REMOTE_UNINSTALL" 120 2>&1)
    RC=$?
else
    info "root shell is off; acquiring root to uninstall (20-60 seconds)"
    OUT=$(cd "$ROOT/vendor/samsung_tv_root" && \
        PYTHONPATH=src "$PYTHON" -m samsung_tv_root archive-root root "$TV_IP" \
            --runtime 3.1 --sdb-timeout 30 --accept-timeout 60 \
            --command-timeout 120 --callback-host "$HOST_IP" \
            --payload-directory "$ROOT/vendor/payloads/common31" \
            --command "$REMOTE_UNINSTALL" 2>&1)
    RC=$?
fi
set -e

if ! printf '%s\n' "$OUT" | grep -q 'SAMPWN_UNINSTALL_OK'; then
    printf '%s\n' "$OUT" | tail -30 >&2
    die "remote uninstall failed (exit $RC)"
fi
printf '%s\n' "$OUT" | grep -E 'original parseinfo|no parseinfo|no SamPwn hook|WARNING' || true

WIDGET_RESULT="uninstalled"
if [ "$KEEP_WIDGET" = 0 ]; then
    if ! (cd "$ROOT/app" && tizen uninstall -p "$PACKAGE" -s "$TV_IP:26101" 2>&1); then
        warn "widget uninstall failed; remove SamPwn from the TV apps screen manually"
        WIDGET_RESULT="not uninstalled (see warning)"
    fi
else
    info "leaving the widget installed (--keep-widget)"
    WIDGET_RESULT="kept (--keep-widget)"
fi

sleep 2
cat <<EOF

SamPwn has been removed.

  * /opt/etc/parseinfo.sh restored from parseinfo.orig.sh (if available)
  * sampwn.service / sampwn-ui.service removed
  * root shell stopped
  * widget $WIDGET_RESULT
  * /opt/etc/sampwn $([ "$KEEP_DATA" = 1 ] && echo 'kept' || echo 'removed')

Reboot the TV once to confirm that it starts normally with no root hook.
EOF
