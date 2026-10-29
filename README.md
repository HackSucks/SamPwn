# SamPwn

SamPwn gives a rooted Samsung Tizen TV a small, persistent control service, a
LAN root shell that you can switch off from the TV, and a dashboard with a
one-shot ASCII-art show.

It is built for **your own TV**, on your own network. It is not a remote
exploit and it does not bypass Developer Mode.

```
 TV (root)                                      Laptop
 ┌──────────────────────────────┐                ┌──────────────────────┐
 │ sampwn.service               │                │ install.sh           │
 │  ├─ control API 127.0.0.1:2324│◀── dashboard ──│  ├─ serves files     │
 │  └─ root shell  0.0.0.0:2323 │                │  ├─ tizen install    │
 │ sampwn-ui.service            │                │  └─ root acquisition │
 │  └─ SamPwn dashboard         │                └──────────────────────┘
 │ /opt/etc/parseinfo.sh hook   │
 └──────────────────────────────┘
```

## What you get

* **Boot persistence without the laptop.** A boot hook starts the daemon and
  the dashboard every time the TV powers on.
* **Dashboard** (`SamPwn` app) with shell status, a root-shell on/off toggle,
  device status, a manual show trigger, and a clean exit to TV.
* **Root shell on TCP 2323.** It is enabled by default, but the dashboard can
  close it without rebooting. The setting survives reboots.
* **Localhost-only control API on 127.0.0.1:2324.** The dashboard talks to the
  daemon there; the API is not exposed to the LAN.
* **One-shot show** (`pwned.html`) on the first launch after boot. Press Back
  to return to the dashboard.
* **Installer and uninstaller** with SHA-256 verification and a restore path
  for the original boot hook.

## Supported Tizen versions

| Platform | Status | Notes |
|---|---|---|
| **Tizen 6.0**, Samsung 2021 AU7 series | **Verified** | Tested on `GU43AU7179UXZG`, build `21_KANTSU2E_UHD_BASIC`, kernel 4.1.10 armv7l, .NET Core 3.1, Chromium 76. Root, boot hook, dashboard, show, and cold boot verified. |
| Other Samsung **Tizen 6.0** TVs | **Compatible if the probe passes** | Requires an unpatched `sdbd-tarlauncher`, effective `cap_setuid`/`cap_setgid`, `/usr/bin/dotnet` 3.1 with `cap_setgid`/`cap_sys_admin`, and a `tar` that supports `--to-command`. Run the probe before installing. |
| Samsung **Tizen 5.5** | **Untested** | May work if .NET Core 3.1 and the same launcher capabilities exist. Not validated by this project. |
| Samsung **Tizen 6.5 / 7.0 / 8.0+** | **Not supported by SamPwn** | Some newer models still expose the archive-launcher route and some reject the SDB foothold. SamPwn only ships the .NET Core 3.1 `common31` payload and has not been tested there. Try the probe first; do not assume it works. |
| Samsung **Tizen 4.0 / 5.0 and older** | **Not supported** | No tested .NET Core 3.1 payload path. |
| Non-Samsung Tizen TVs | **Not supported** | Different platform and packaging. |

"Verified" means tested end-to-end on the listed model. "Compatible if the
probe passes" is a capability check, not a guarantee. See
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the exact probe and the
failure messages.

## Requirements

On the laptop:

* Linux with Bash, `curl`, `openssl`, `sha256sum`, and **Python 3.12+**
* [Tizen Studio](https://developer.tizen.org/development/tizen-studio/download)
  CLI, with `sdb` and `tizen` on `PATH` (or set `TIZEN_STUDIO`)
* A network route to the TV, and a laptop firewall that allows the TV to reach
  the installer's HTTP/callback ports

On the TV:

* Developer Mode enabled
* The laptop's IP allowed in the Developer Mode host list
* The sdb authorization prompt accepted on the TV

## Quick start

```bash
git clone https://github.com/HackSucks/SamPwn.git SamPwn
cd SamPwn
./install.sh --tv 192.168.1.93
```

The installer will:

1. connect with `sdb`;
2. install the signed `SamPwn.wgt` widget;
3. serve the daemon and boot hook over HTTP;
4. acquire root and verify every downloaded file with SHA-256;
5. back up the original `/opt/etc/parseinfo.sh` to
   `/opt/etc/parseinfo.orig.sh`;
6. install the boot hook and start the daemon;
7. launch the dashboard.

Useful options:

```text
--tv IP          TV address (or TV_IP)
--host IP        laptop address reachable from the TV (auto-detected)
--port N         preferred HTTP port (default 8000; a free port is chosen if busy)
--shell on|off   leave the LAN root shell enabled or disabled (default on)
--no-show        disable the automatic one-shot show
```

The root shell is intentionally left **on** by default so the installer can
verify the result. Turn it off in the dashboard when you are done:

> Dashboard → Root shell → **Turn root shell OFF**

The setting is stored in `/opt/etc/sampwn/shell.state` and is applied at boot.

## Verify

From the laptop:

```bash
python3 tools/rootshell.py --host 192.168.1.93 'id'
# uid=0(root) gid=0(root) context="User"

python3 tools/rootshell.py --host 192.168.1.93 \
  'curl -fsS http://127.0.0.1:2324/status'
```

Expected status fields include `"shell":true`, `"hook":true`, and
`"widget":true` when the shell is enabled and the installation is healthy.

To confirm boot persistence, power-cycle the TV (a full power cycle, not just
standby), wait for the dashboard to appear, then run the `id` check again.

## Security

This is real root. The LAN shell is a passwordless, unauthenticated root shell
on port 2323. Anyone who can reach that port can control the TV.

* Keep the TV on a trusted VLAN or network.
* Leave the shell **off** unless you are using it.
* The control API is bound to `127.0.0.1` and is not reachable from the LAN.
* Do not expose ports 2323 or 2324 to the internet.
* Enable the kill switch (`touch /opt/etc/sampwn/disabled`) before a reboot if
  you want the boot hook to skip all SamPwn payloads.

Read [docs/SECURITY.md](docs/SECURITY.md) before installing.

## Uninstall

```bash
./uninstall.sh --tv 192.168.1.93
```

This restores `/opt/etc/parseinfo.sh` from `parseinfo.orig.sh`, removes the
transient units, stops the daemon and shell, and uninstalls the widget. Use
`--keep-data` to preserve `/opt/etc/sampwn` and `--keep-widget` to leave the
widget installed. See [docs/UNINSTALL.md](docs/UNINSTALL.md).

## How it works

1. **Root acquisition** uses the vendored `samsung-tv-root` archive launcher
   (public domain). SamPwn patches it to detach the launcher from the sdb
   transport, so later `sdb` commands are not blocked for the whole session.
2. **Persistence** replaces `/opt/etc/parseinfo.sh`, which `AT-HUB.service`
   runs as root at boot. The replacement is an unsigned gawk script: the TV's
   UEP check blocks unsigned shell/node scripts, but gawk is not patched.
3. **Transient units** are written to `/run/systemd/system/` on each boot and
   started with `systemctl start --no-block`. `/run` is tmpfs, so nothing
   stale survives a reboot.
4. **The daemon** (`SampwnSrv.dll`, .NET Core 3.1) runs as root, serves the
   dashboard API on `127.0.0.1:2324`, and opens the LAN shell on
   `0.0.0.0:2323` only while enabled.
5. **The widget** polls the API with small `XMLHttpRequest` calls and uses the
   TV remote through the Tizen input-device API.

There is no `sdb shell`, `sdb root on`, or `sdb push` dependency at runtime.
The installer uses `sdb` only for device discovery and the one-time injection.

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Repository layout

```text
app/                  widget sources, show, audio, font, built SamPwn.wgt
sampwn-srv/           C# daemon source (.NET Core 3.1)
dist/srv/             prebuilt daemon used by the installer
boothook/parseinfo.sh boot hook (gawk)
tools/                rootshell.py and serve.py
vendor/               patched samsung-tv-root source, common31 payloads, licenses
docs/                 install, compatibility, security, troubleshooting, uninstall
install.sh            laptop installer
uninstall.sh          laptop uninstaller
SHA256SUMS            hashes of every committed release file
```

## Building from source

The daemon builds with any .NET SDK that can target `netcoreapp3.1`:

```bash
dotnet build sampwn-srv/SampwnSrv.csproj -c Release
cp sampwn-srv/bin/Release/netcoreapp3.1/SampwnSrv.{dll,deps.json,runtimeconfig.json} dist/srv/
```

The widget is built with Tizen Studio and a TV signing profile:

```bash
cd app
tizen package -t wgt -s tv -- .
```

Then install the new `app/SamPwn.wgt`. See [docs/INSTALL.md](docs/INSTALL.md#building).

## Credits

* Root acquisition and payloads:
  [`samsung-tv-root`](https://github.com/chris-ritsen/samsung-tv-root) by its
  authors, released to the public domain (Unlicense). SamPwn vendors a patched
  copy under `vendor/`.
* Font: Adwaita Mono, Copyright 2015-2026 Renzhi Li, SIL Open Font License
  1.1. A printable-ASCII subset is bundled as `app/sampwn-mono.ttf`.
* Show audio: `app/pwned.mp3` is part of this project's show assets. Check
  that you have the right to redistribute it before publishing a fork.

See [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md) and [LICENSE](LICENSE).

## License

SamPwn is released under the MIT License. Vendored components keep their own
licenses. This project is not affiliated with Samsung.
