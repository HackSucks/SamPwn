# Architecture

SamPwn is deliberately small. It has four pieces:

1. a host installer;
2. a root-acquisition path used only during install/uninstall;
3. a TV-side daemon;
4. a Tizen widget.

## Components

```text
install.sh
  ├─ sdb/tizen: connect, install widget
  ├─ tools/serve.py: temporary HTTP file server
  └─ vendor/samsung_tv_root: root acquisition
        └─ sends a remote shell command to the TV

TV
  ├─ /opt/etc/parseinfo.sh          boot hook (gawk, runs as root)
  ├─ /run/systemd/system/sampwn.service
  │    └─ SampwnSrv.dll            root daemon
  │         ├─ 127.0.0.1:2324      control API
  │         ├─ 0.0.0.0:2323        raw root shell (toggleable)
  │         └─ 0.0.0.0:2525        telnet terminal (toggleable)
  ├─ /run/systemd/system/sampwn-ui.service
  │    └─ app_launcher sampwnroot.dashboard
  └─ SamPwn widget
       ├─ index.html               dashboard
       └─ pwned.html               one-shot show
```

## Install flow

1. `install.sh` finds the TV through `sdb`.
2. It installs `app/SamPwn.wgt` with `tizen install`.
3. It stages the daemon and boot hook in a temporary directory and starts
   `tools/serve.py` on the laptop.
4. It builds a remote shell command containing the expected SHA-256 values of
   every file.
5. If a root shell is already open on 2323, the command is sent through it.
   Otherwise the vendored exploit acquires root and runs the command.
6. On the TV the command downloads each file with `curl`, verifies the hash,
   keeps the original `parseinfo.sh` as `parseinfo.orig.sh`, and atomically
   moves the new files into place.
7. The hook is executed once to create the transient units and start the
   daemon. A detached restart loads the new binary after the command returns.
8. The installer verifies `/status` and launches the dashboard.

### Why HTTP instead of `sdb push`

The tested TV's `sdb push` is unreliable and can block later `sdb` operations.
The TV has `curl`, so the installer serves files from the laptop and verifies
them with SHA-256. `tizen install` is used for the signed widget because the
widget manager owns that path.

### Why the launcher is detached

The upstream archive launcher normally stays attached to the `sdb` transport
for the whole root session. `sdbd` runs one shell command at a time, so that
would block every later injection. SamPwn's vendored patch starts the launcher
with `setsid`, closes the inherited callback fd 3, and waits for its exit log
without holding the sdb shell open.

## Boot flow

`AT-HUB.service` starts `/opt/etc/parseinfo.sh &` as root. The script:

1. writes the original target-version marker;
2. exits early if `/opt/etc/sampwn/disabled` exists;
3. writes `sampwn.service` and `sampwn-ui.service` to
   `/run/systemd/system/`;
4. on a fresh install, arms `show.armed` (the hook turns it into
   `show.pending` exactly once); later boots stay quiet;
5. starts both units with `systemctl start --no-block`.

`/run` is tmpfs, so the units are recreated on every boot. Brand-new unit files
are loaded on demand; no `daemon-reload` is required. The units have bounded
start limits so a failing payload cannot create a boot loop.

### Why gawk

The TV's UEP check rejects unsigned shell and Node scripts executed by systemd.
The boot hook uses:

```text
#!/usr/bin/gawk -f
```

The TV's gawk is not UEP-patched. Commands are issued through gawk's
`system()`, which uses an inline `sh -c` string; inline strings are not treated
as script files by UEP. This is a compatibility workaround, not a security
boundary.

## Daemon

`SampwnSrv.dll` targets .NET Core 3.1 and .NET 6+. The .NET Core 3.1 build
runs on older TVs; the .NET 6 build rolls forward to 6, 7, 8, and 9. The boot
hook picks the right build at boot from the active `.NET` runtime and runs it
from `/opt/etc/sampwn/net31/` or `/opt/etc/sampwn/net60/`.

### State

* `/opt/etc/sampwn/net31/` — daemon for .NET Core 3.1 TVs.
* `/opt/etc/sampwn/net60/` — daemon for .NET 6+ TVs.
* `/opt/etc/sampwn/shell.state` — `on` or `off`; missing means `on`.
* `/opt/etc/sampwn/show.armed` — one-time arm for the first-root intro (fresh installs).
* `/opt/etc/sampwn/show.pending` — exists while the show is queued.
* `/opt/etc/sampwn/show.done` — records that the intro has played.
* `/opt/etc/sampwn/no-show` — suppresses the automatic one-shot show; the
  dashboard button still works.
* `/opt/etc/sampwn/sampwn.log` — append-only, rotated at 256 KiB.
* `/opt/etc/sampwn/disabled` — checked only by the boot hook.

### Control API (`127.0.0.1:2324`)

| Method | Path | Effect |
|---|---|---|
| `GET` | `/status` | JSON status used by the dashboard |
| `GET` | `/health` | `ok` |
| `POST` | `/shell/on` | Enable the LAN shell and persist `on` |
| `POST` | `/shell/off` | Stop the shell and persist `off` |
| `POST` | `/show/pending` | Queue the show |
| `POST` | `/show/consume` | Clear the queued show and mark it done |
| `POST` | `/log` | Append a short diagnostic line |

`/status` returns fields including:

```json
{"ok":true,"shell":true,"telnet":true,"listening":true,"clients":0,
 "port":2323,"telnet_port":2525,"telnet_clients":0,
 "ip":"192.168.1.93","uptime":12.3,
 "show_pending":false,"show_done":true,"hook":true,"widget":true,
 "pid":1492,"version":"1.2.0"}
```

The API is loopback-only. It has no command-execution endpoint.

### Root shell (`0.0.0.0:2323`)

`ShellServer` accepts TCP clients on `0.0.0.0:2323` and runs
`/usr/bin/script -qfc /bin/bash /dev/null`, giving an interactive root bash.
`TelnetServer` accepts TCP clients on `0.0.0.0:2525`, negotiates telnet
options (ECHO, SGA, TTYPE, NAWS), allocates a PTY, and runs bash so Ctrl-C,
Ctrl-Z, job control, colours, and window size all work. Both track clients and
child processes so `POST /shell/off` can close everything immediately. The
listeners are only created while the shell is enabled; the retry loop restarts
them if a stale port frees up.

## Widget

Package `sampwnroot`, app ID `sampwnroot.dashboard`, display name `SamPwn`.
Tizen rejects package IDs containing dots and very short names, so the package
is `sampwnroot` while the app ID keeps the usual dotted form.

`index.html` (dashboard):

* polls `/status` every 1.5 seconds with `XMLHttpRequest`;
* renders solid-color cards and plain CSS (no gradients, shadows, or
  WebSocket churn) for speed on Chromium 76;
* exposes the shell toggle, refresh, show queue, and exit;
* maps arrow keys to card focus and Enter to activate;
* consumes a queued show and navigates to `pwned.html?auto=1`.

`pwned.html` (show):

* runs an independent wall-clock timeline, so visuals do not stall if audio is
  blocked;
* plays `pwned.mp3` (4:57) from the widget, starting on the first remote key if
  autoplay is refused;
* renders figlet ASCII art on a canvas using the bundled `sampwn-mono.ttf`;
* waits for `document.fonts.load` before the first layout so the art is a
  monospace grid;
* exits to `index.html` on Back, Escape, click after start, or audio end.
* posts `/show/consume` when opened with `?auto=1`.

The show is **first-root-only**: the boot hook arms it once on a fresh install,
then converts that arm into `show.pending` exactly once. After the show plays,
`show.done` is recorded and later boots stay quiet. The dashboard can still
replay it on demand from the Show tab.

### Why the font is bundled

`fc-match monospace` on the tested TV returns SamsungOneUI, a proportional
font. Drawing figlet output with it collapses the ASCII grid. The widget ships
an 11 KB subset of Adwaita Mono covering printable ASCII. The subset is
licensed under the SIL Open Font License; see
[`app/FONT-LICENSE.txt`](../app/FONT-LICENSE.txt).

## Failure behavior

* Missing daemon or widget: the corresponding transient unit is not started
  (`ConditionPathExists`).
* Widget not installed: `sampwn-ui.service` is a no-op.
* `sampwn-ui.service` retries `app_launcher` up to 20 times at 3-second
  intervals, then gives up. It cannot block boot.
* `sampwn.service` retries at most three times in 60 seconds.
* Shell bind failure: the daemon logs it and retries every 5 seconds.
* A malformed hook exits without touching any boot target.

## Deliberate non-goals

* No internet access, update checker, or telemetry.
* No generic remote command API on the LAN.
* No firmware extraction or modification.
* No support for TVs that fail the preflight.
* No hidden persistence outside `/opt/etc/parseinfo.sh` and `/opt/etc/sampwn`.
