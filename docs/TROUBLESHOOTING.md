# Troubleshooting

Most failures are one of: `sdb` is not connected, the TV cannot reach the
laptop, or the TV is asleep in network standby.

## Installer fails before root acquisition

### `sdb does not see TV_IP:26101`

```bash
sdb kill-server
sdb start-server
sdb connect TV_IP:26101
sdb devices
```

* Confirm Developer Mode is still enabled.
* Confirm the laptop IP is in the TV's Developer Mode host list.
* Accept the authorization prompt on the TV.
* Try the TV's current IP; DHCP may have changed it.

`unknown` in the `sdb devices` state column means the TV has not authorized the
laptop yet.

### `widget install failed`

* Developer Mode may have been disabled by a TV update.
* The TV storage may be full.
* A previous package with the same ID may be stuck. Check the TV's app list and
  remove `SamPwn` manually, then retry.
* Tizen Studio may be using a different `sdb` server. Make sure `sdb` and
  `tizen` come from the same Tizen Studio installation.

### `Python 3.12 or newer is required`

Install a newer Python or point the installer at it:

```bash
./install.sh --tv 192.168.1.93   # after putting python3.12+ on PATH
PYTHON=/usr/bin/python3.13 ./install.sh --tv 192.168.1.93
```

### `sdb push` is slow or hangs

SamPwn does not use `sdb push`. If an older tool or a manual command is stuck,
close it. `sdbd` serializes shell commands, so one stuck command can block the
next one until the TV reboots.

## Installer starts but remote install fails

Look for `SAMPWN_INSTALL_FAIL` in the output.

### `download ...` failed

The TV could not reach the laptop HTTP server. Check:

* the installer printed the right `--host` address;
* the TV and laptop are on the same subnet;
* the laptop firewall allows the TV to connect to the printed port;
* no VPN is overriding the route;
* the HTTP server was still running (the installer keeps it until exit).

Retry with an explicit address and port:

```bash
./install.sh --tv 192.168.1.93 --host 192.168.1.171 --port 8000
```

### `verify ...` failed

A file changed in transit or the server served a stale file. Retry. If it
persists, check for a proxy or captive portal on the laptop's network.

### `backup hook` failed or the installer stopped at the hook

The installer will not overwrite a non-SamPwn hook without a backup. Check
`/opt/etc` free space and permissions through the root shell. Do not delete
`/opt/etc/parseinfo.orig.sh`.

### `daemon not responding`

The files are installed but `SampwnSrv.dll` did not start. Check through the
root shell:

```bash
systemctl status sampwn.service --no-pager
cat /opt/etc/sampwn/sampwn.log
/usr/bin/dotnet /opt/etc/sampwn/SampwnSrv.dll
```

Common causes:

* the TV's .NET runtime is not 3.1;
* the daemon files were truncated;
* another process holds port 2324;
* `/opt/etc/sampwn` is not writable.

### `no authenticated root callback`

The TV ran the launcher but could not connect back to the laptop. The root
callback port is opened by the vendored exploit. Allow the TV's subnet through
the laptop firewall and retry. This is also the usual failure when the TV's
firmware has patched the launcher.

## Root shell problems

### `Connection refused` on 2323

The shell is off, the daemon is not running, or the TV is in standby.

On the TV:

1. open the SamPwn dashboard;
2. check that **Root shell** says `ON`;
3. if the dashboard says `control offline`, the daemon is not running.

Through `sdb` or another root path:

```bash
cat /opt/etc/sampwn/shell.state
systemctl status sampwn.service --no-pager
tail -n 50 /opt/etc/sampwn/sampwn.log
```

### The shell turns itself back on after a reboot

`/opt/etc/sampwn/shell.state` is `on`, or the file is missing (missing defaults
to `on`). Turn it off in the dashboard, or write the state explicitly:

```bash
printf 'off\n' > /opt/etc/sampwn/shell.state
curl -fsS -X POST http://127.0.0.1:2324/shell/off
```

The dashboard does not re-enable the shell on launch; it only changes the state
when you press the toggle. The installer and a manual `/shell/on` call do
enable it.

### The shell works after install but not after a cold boot

Check the boot hook and the transient unit:

```bash
head -1 /opt/etc/parseinfo.sh          # should be #!/usr/bin/gawk -f
ls -l /run/systemd/system/sampwn.service
systemctl status sampwn.service --no-pager
ls -l /opt/etc/sampwn/disabled          # if present, the hook skips SamPwn
```

If the unit is missing, the hook did not run. Run `/opt/etc/parseinfo.sh`
manually to test it, then check `AT-HUB.service`:

```bash
systemctl status AT-HUB.service --no-pager
```

### `systemctl` says the unit is not found

The units live in `/run/systemd/system` and are created at boot. If they are
missing, run the hook:

```bash
/opt/etc/parseinfo.sh
```

If `sampwn.service` exists but was changed manually, run
`systemctl daemon-reload` before restarting it.

## Dashboard problems

### Dashboard is slow or the TV is swapping

The 1.1.0 dashboard is lightweight: solid cards, no gradients/shadows, 1.5 s
polling, and a 52 KB show HTML with external audio. If an old build is still
installed, reinstall the widget:

```bash
cd app
tizen install -n SamPwn.wgt -s TV_IP:26101 -- .
```

Check for runaway processes through the root shell:

```bash
top -b -n1 | head -20
```

### `control offline`

The daemon is stopped, restarting, or bound to a different port.

```bash
systemctl restart sampwn.service
curl -fsS http://127.0.0.1:2324/status
```

The dashboard also reports `control offline` while the daemon restarts after an
installer update.

### `widget: missing` in `/status`

The daemon looks for
`/opt/usr/apps/sampwnroot/res/wgt/index.html`. Reinstall the widget; do not
rename its package.

### Remote keys do not work in the show

The show registers media and color keys through `tizen.tvinputdevice`. If the
TV firmware ignores a key, use the arrow/Enter keys or Back. Back always exits
the show.

### No audio

Chromium 76 may refuse autoplay. The show starts visuals on its own clock and
shows `press any key for sound`; press any remote key. Also check the TV volume
and that `pwned.mp3` is present in the widget.

### ASCII art is broken

This means the TV fell back to a proportional font. Reinstall the widget; the
1.1.0 package contains `sampwn-mono.ttf`. Confirm the font is present:

```bash
python3 tools/rootshell.py --host TV_IP \
  'ls -l /opt/usr/apps/sampwnroot/res/wgt/sampwn-mono.ttf'
```

## TV boot and recovery

The boot hook is non-fatal by design. A broken payload should mean "no root
shell", not "TV will not boot".

To disable the hook before a reboot:

```bash
touch /opt/etc/sampwn/disabled
```

If the dashboard and shell are both unreachable after a firmware update:

1. connect with `sdb`;
2. install the widget again if the app is gone;
3. run `./uninstall.sh --tv TV_IP` from the laptop — it reacquires root through
   the injection and restores `parseinfo.orig.sh`;
4. if the injection no longer works, the TV cannot be cleaned remotely. A
   factory reset or service intervention may be required.

## Collecting a useful report

Include:

* `VERSION`;
* TV model, Tizen build, kernel, architecture;
* `sdb devices` output;
* installer output with the `SAMPWN_*` markers;
* `/opt/etc/sampwn/sampwn.log`;
* `systemctl status sampwn.service` and `sampwn-ui.service`;
* `/status` JSON;
* whether the failure happens before or after a cold boot.

Do not include your Wi-Fi password, Tizen certificate passwords, or private
keys.
