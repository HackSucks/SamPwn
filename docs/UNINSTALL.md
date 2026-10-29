# Uninstalling SamPwn

The uninstaller restores the original boot hook, removes the daemon and
transient units, stops the root shell, and uninstalls the widget.

```bash
./uninstall.sh --tv 192.168.1.93
```

If the root shell is off, the script reacquires root through the same injection
used by the installer. It needs the laptop to reach the TV, and the TV to reach
the laptop's callback port.

## Options

| Flag | Meaning |
|---|---|
| `--tv IP` | TV address. Required unless `TV_IP` is set. |
| `--host IP` | Laptop address reachable from the TV. |
| `--keep-data` | Keep `/opt/etc/sampwn` (state, logs, daemon files). |
| `--keep-widget` | Leave the `SamPwn` widget installed. |

Default behavior removes `/opt/etc/sampwn`. The backup
`/opt/etc/parseinfo.orig.sh` is preserved, not deleted.

## What the uninstaller does

1. Restores `/opt/etc/parseinfo.sh` from `/opt/etc/parseinfo.orig.sh`.
2. Stops `sampwn-ui.service`.
3. Removes `/run/systemd/system/sampwn.service` and
   `sampwn-ui.service` and runs `systemctl daemon-reload`.
4. Removes `/opt/etc/sampwn` unless `--keep-data` was used.
5. Schedules the daemon stop **last**, so the live root shell can finish the
   command and return its result.
6. Uninstalls the Tizen package `sampwnroot` unless `--keep-widget` was used.

A successful run prints:

```text
original parseinfo.sh restored
SAMPWN_UNINSTALL_OK
```

Then reboot the TV and confirm that it starts normally with no root shell and
no dashboard launch.

## If there is no backup

If `/opt/etc/parseinfo.orig.sh` is missing, the uninstaller removes the SamPwn
hook only when it can confirm the SamPwn marker. In that case the TV may no
longer have a `parseinfo.sh`, which is usually harmless: `AT-HUB.service` has
no dependents and its failure does not block boot. If you want to restore the
original file, copy it from a backup or reinstall the firmware.

Do not delete `parseinfo.orig.sh` manually. It is the only copy of the
original hook.

## Manual uninstall

If the script cannot run, use an existing root shell:

```bash
# as root, over port 2323
systemctl stop sampwn-ui.service
cp -p /opt/etc/parseinfo.orig.sh /opt/etc/parseinfo.sh
rm -f /run/systemd/system/sampwn.service /run/systemd/system/sampwn-ui.service
systemctl daemon-reload
rm -rf /opt/etc/sampwn
# last: this kills the shell you are using
systemctl stop sampwn.service
```

If you want the shell to survive until you are done, run the last command in a
detached process:

```bash
( sleep 2; systemctl stop sampwn.service ) >/dev/null 2>&1 &
```

Then uninstall the widget from the laptop:

```bash
tizen uninstall -p sampwnroot -s TV_IP:26101
```

or remove **SamPwn** from the TV's app screen.

## Keeping the widget

`--keep-widget` leaves the dashboard installed. This is useful if you want to
keep the UI but are changing the daemon. The dashboard will show
`control offline` until a daemon is running again. The boot hook is still
restored, so the TV will not start SamPwn automatically.

## Keeping data

`--keep-data` leaves:

```text
/opt/etc/sampwn/SampwnSrv.dll
/opt/etc/sampwn/SampwnSrv.deps.json
/opt/etc/sampwn/SampwnSrv.runtimeconfig.json
/opt/etc/sampwn/shell.state
/opt/etc/sampwn/sampwn.log
```

The boot hook is restored either way, so these files do nothing until you
reinstall or start the daemon manually. This is intended for debugging and for
quick reinstall.

## Kill switch instead of uninstall

If you only want to stop SamPwn from starting but keep it installed:

```bash
touch /opt/etc/sampwn/disabled
```

The next boot performs only the original parseinfo work. Remove the file to
re-enable. The dashboard and shell will not start while the marker exists.

## Reinstall after uninstall

The installer is idempotent:

```bash
./install.sh --tv 192.168.1.93
```

If you used `--keep-data`, the existing `shell.state` is replaced with the
installer's `--shell` value. The original hook backup is preserved and is not
overwritten.
