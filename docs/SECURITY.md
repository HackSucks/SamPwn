# Security

SamPwn provides **real, persistent root** on a TV you own. Treat the TV as a
root server on your LAN, not as an appliance.

## Threat model

SamPwn assumes:

* the TV and laptop are on a trusted home or lab network;
* the TV has Developer Mode enabled for installation;
* only trusted people can install Tizen apps on the TV;
* the TV is not exposed directly to the internet;
* the user understands that the root shell is unauthenticated.

SamPwn does **not** protect against:

* someone with physical access to the TV;
* someone who can already run code as root or as another Tizen app on the TV;
* a compromised router or hostile LAN;
* Samsung firmware updates that remove the exploit;
* data exfiltration by third-party apps installed on the TV.

## Root shell on TCP 2323

The daemon opens `/bin/bash` on `0.0.0.0:2323` while the shell is enabled.
There is **no password, no token, and no client authentication**. Anyone who can
open a TCP connection to the TV on port 2323 gets a root shell.

Mitigations:

* keep the TV on a trusted VLAN or subnet;
* turn the shell **off** in the dashboard when not in use;
* never port-forward 2323 from the internet;
* block 2323 at the router/firewall for untrusted devices;
* use `--shell off` when installing a TV that does not need shell access;
* reboot if you suspect a session is still open (the daemon also closes active
  clients when the shell is turned off).

`tools/rootshell.py` is a convenience client, not an authentication layer.

## Control API on 127.0.0.1:2324

The control API is bound to the loopback interface only. It is not reachable
from the LAN. It has no authentication because it is intended for the local
dashboard.

Residual risk: another application or web page running **on the TV** can reach
`127.0.0.1:2324`. The API sends permissive CORS headers so the `file://` widget
can use it. A malicious TV app could therefore toggle the shell or queue the
show. The API cannot read files, execute commands, or return the shell contents;
the dangerous capability is the shell toggle itself. Do not install untrusted
Tizen apps on a TV with SamPwn.

The API does not expose a generic command-execution endpoint.

## Boot hook

`/opt/etc/parseinfo.sh` is replaced with a gawk script. It runs as root at boot
through `AT-HUB.service`. It:

* preserves the original `/proc/target_version` write;
* checks for the kill switch at `/opt/etc/sampwn/disabled`;
* writes transient units to `/run/systemd/system/`;
* starts the daemon and dashboard;
* queues the show.

The original hook is saved once as `/opt/etc/parseinfo.orig.sh`. The installer
does not overwrite an existing backup.

The hook is designed to fail closed: if a payload is missing, systemd refuses
to start that unit. There is no wait loop and no dependency on `AT-HUB.service`
finishing, so a failure cannot block the normal boot target.

## Kill switch

Before rebooting, create the kill switch through the root shell:

```bash
touch /opt/etc/sampwn/disabled
```

On the next boot the hook performs only the original `parseinfo.sh` work and
does not start the daemon, shell, or dashboard. Remove the file to re-enable.
The shell state file is independent of the kill switch.

If the shell is already off and you cannot get root, use `uninstall.sh` (it can
reacquire root through the same injection) or restore `parseinfo.orig.sh`
through another root path.

## Files and data

SamPwn stores only operational state:

| File | Contents |
|---|---|
| `/opt/etc/sampwn/shell.state` | `on` or `off` |
| `/opt/etc/sampwn/show.pending` | timestamp when the show is queued |
| `/opt/etc/sampwn/no-show` | present when automatic show playback is disabled |
| `/opt/etc/sampwn/sampwn.log` | daemon events, rotated at 256 KiB |

The log can contain connection counts, shell state changes, and errors. It does
not log shell commands or file contents. There is no telemetry and no network
callout to the internet.

The installer's temporary HTTP server serves only the deploy files. It is
stopped when the installer exits and does not run at boot.

## Firmware and update risk

Samsung firmware updates can remove the root route or the boot hook. After an
update, the TV may still boot normally but SamPwn will not. Re-run the
installer and check the preflight. Do not attempt to downgrade or flash
firmware as part of SamPwn; that is outside this project's scope and carries a
high bricking risk.

## Legal and warranty

Rooting may void the TV's warranty and may violate the terms of service of
services used on the TV. Use SamPwn only on hardware you own and only where
you are allowed to modify it. The authors are not responsible for damage,
data loss, bricked TVs, or account problems.

## Reporting a security issue in SamPwn

If you find a vulnerability in SamPwn itself (not in Samsung's firmware), open
a private security report with the maintainers rather than a public issue.
Include the version, TV model, steps to reproduce, and impact.
