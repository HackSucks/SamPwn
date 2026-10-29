# Installing SamPwn

SamPwn is installed from a laptop on the same network as the TV. The installer
uses `sdb` and `tizen` for the one-time widget install and root injection, then
the TV fetches the root payloads over HTTP from the laptop. At runtime the TV
does not need the laptop.

## 1. Prepare the TV

1. On the TV, open **Apps**.
2. Enter `12345` with the remote.
3. Enable **Developer mode**.
4. Add the laptop's LAN IPv4 address as a host PC.
5. Reboot the TV (Developer Mode is applied at boot).
6. Reconnect with `sdb`:

   ```bash
   sdb connect TV_IP:26101
   sdb devices
   ```

   The first connection shows an authorization prompt on the TV. Accept it.

`sdb shell` and `sdb root on` are not used by SamPwn and are known not to work
on the tested TV. Do not be alarmed if they fail.

## 2. Prepare the laptop

Install:

* Bash, `curl`, `openssl`, `sha256sum`
* Python **3.12 or newer**
* Tizen Studio with the CLI tools

Tizen Studio usually installs to `$HOME/tizen-studio`. If `sdb` and `tizen` are
not on `PATH`, point the installer at the installation:

```bash
export TIZEN_STUDIO="$HOME/tizen-studio"
export PATH="$TIZEN_STUDIO/tools/ide/bin:$TIZEN_STUDIO/tools:$PATH"
```

Check:

```bash
sdb version
tizen version
python3 -V
openssl version
```

The installer does not create a virtualenv and does not download Python
packages. The vendored exploit has no third-party runtime dependencies.

## 3. Network and firewall

Two short-lived connections matter:

* the installer's HTTP server on the laptop (prefers TCP 8000, falls back to a
  free port);
* the root callback server opened by the vendored exploit.

The TV must be able to reach the laptop on those ports. If the laptop runs a
host firewall, allow the TV's subnet, or temporarily allow the chosen HTTP
port. The installer prints the address it uses.

Do not run the installer over a VPN interface unless the TV can route to that
address. If auto-detection picks the wrong interface, pass `--host`:

```bash
./install.sh --tv 192.168.1.93 --host 192.168.1.171
```

## 4. Install

```bash
cd SamPwn
./install.sh --tv 192.168.1.93
```

Optional flags:

| Flag | Meaning |
|---|---|
| `--tv IP` | TV address. Required unless `TV_IP` is set. |
| `--host IP` | Laptop address the TV can reach. |
| `--port N` | Preferred HTTP port. Default 8000. |
| `--shell on` | Leave the LAN root shell enabled after install (default). |
| `--shell off` | Disable the LAN root shell after install. |
| `--no-show` | Disable the automatic one-shot show (persistent; use the dashboard button to play it manually). |

The installer prints each phase. A successful run ends with:

```text
SAMPWN_INSTALL_OK
[sampwn] status: {"ok":true,"shell":true,...}
SamPwn is installed.
```

If the remote install fails, the output includes a `SAMPWN_INSTALL_FAIL ...`
marker. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### What the installer changes on the TV

| Path | Purpose |
|---|---|
| `/opt/etc/parseinfo.sh` | Replaced with the gawk boot hook. |
| `/opt/etc/parseinfo.orig.sh` | One-time backup of the original hook. Never overwritten. |
| `/opt/etc/sampwn/SampwnSrv.dll` | Root daemon. |
| `/opt/etc/sampwn/SampwnSrv.deps.json` | Daemon dependencies. |
| `/opt/etc/sampwn/SampwnSrv.runtimeconfig.json` | Daemon runtime config. |
| `/opt/etc/sampwn/shell.state` | `on` or `off`; missing means `on`. |
| `/opt/etc/sampwn/show.pending` | Queues the one-shot show. |
| `/opt/etc/sampwn/no-show` | Disables automatic show playback (`--no-show`). |
| `/opt/etc/sampwn/sampwn.log` | Daemon log (rotated at 256 KiB). |
| `/run/systemd/system/sampwn.service` | Transient daemon unit, recreated each boot. |
| `/run/systemd/system/sampwn-ui.service` | Transient dashboard launcher, recreated each boot. |
| Tizen package `sampwnroot` | The `SamPwn` widget. |

The installer verifies the SHA-256 of every downloaded file on the TV before
moving it into place. The signed widget is installed by `tizen install`; it is
not pushed with `sdb push`.

## 5. Verify

With the shell enabled:

```bash
python3 tools/rootshell.py --host 192.168.1.93 'id'
# uid=0(root) gid=0(root) context="User"

python3 tools/rootshell.py --host 192.168.1.93 \
  'curl -fsS http://127.0.0.1:2324/status'
```

The status JSON should contain:

```json
{"ok":true,"shell":true,"hook":true,"widget":true,"version":"1.1.0"}
```

`hook:true` means `/opt/etc/parseinfo.sh` contains the SamPwn marker.
`widget:true` means the daemon can see the installed dashboard.

## 6. Verify persistence

A full power cycle is different from standby. Unplug the TV or use the TV's
**Power off** / **Restart** menu so the kernel actually reboots, then:

1. wait for the dashboard to appear;
2. run the `id` check again.

The dashboard should come back without the laptop. The first launch after boot
plays `pwned.html` once; press **Back** to return to the dashboard. If you
installed with `--no-show`, automatic playback stays disabled until you delete
`/opt/etc/sampwn/no-show`; use the dashboard's **Play pwned.html** button to run
it deliberately.

## 7. Turn the shell off

In the dashboard:

> **Root shell** → **Turn root shell OFF**

The daemon closes port 2323 immediately and writes `off` to
`/opt/etc/sampwn/shell.state`. The LCD/remote still works normally. Re-enable
it from the dashboard when needed.

With `--shell off`, the installer writes `off` before the daemon starts and
verifies the deployment through the TV-local control API. It does not need to
leave the LAN shell open.

## Updating

Re-running the installer is the supported update path:

```bash
git pull
./install.sh --tv 192.168.1.93 --shell off
```

It backs up the original hook only once, replaces the daemon atomically,
reinstalls the widget, and restarts the daemon.

## Building from source

### Daemon

```bash
dotnet build sampwn-srv/SampwnSrv.csproj -c Release
cp sampwn-srv/bin/Release/netcoreapp3.1/SampwnSrv.dll \
   sampwn-srv/bin/Release/netcoreapp3.1/SampwnSrv.deps.json \
   sampwn-srv/bin/Release/netcoreapp3.1/SampwnSrv.runtimeconfig.json \
   dist/srv/
```

### Widget

Create or select a TV signing profile in Tizen Studio, then:

```bash
cd app
tizen package -t wgt -s tv -- .
```

This produces `app/SamPwn.wgt`. The installer uses that file. Rebuilding
requires the same author certificate that Tizen Studio installed the widget
with; keep the certificate and password outside this repository.

### Reproducing the deploy daemon

The committed `dist/srv/*` files are the exact binaries verified during the
1.1.0 test. Their SHA-256 values are listed in
[THIRD_PARTY.md](THIRD_PARTY.md#build-artifacts). Rebuilding with a different
SDK can produce different bytes, which is fine, but re-test the TV after doing
so.

## Recovery

If the TV still boots but the dashboard is broken, connect with `sdb` and use
the installed widget or run the installer again. If the TV does not start the
dashboard but boots normally, the boot hook is non-fatal by design; root is
simply unavailable until you reinstall.

If you need to stop the boot hook without uninstalling:

```bash
# through the root shell, if it is available
touch /opt/etc/sampwn/disabled
```

On the next boot the hook performs only the original `parseinfo.sh` work and
skips all SamPwn payloads. Remove the file to re-enable.

For a full rollback, see [UNINSTALL.md](UNINSTALL.md).
