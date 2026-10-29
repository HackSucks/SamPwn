# Compatibility

SamPwn combines three independent capabilities. A TV can support one and not
the others:

1. **Root acquisition** — the `SVE-2025-50109` SDB package-name injection plus
   the archive launcher.
2. **Persistence** — `/opt/etc` is writable and `AT-HUB.service` runs
   `/opt/etc/parseinfo.sh` as root at boot.
3. **UI** — Tizen TV widget support and the Tizen input-device API.

The tested configuration has all three.

## Tested configuration

| Item | Value |
|---|---|
| TV model | `GU43AU7179UXZG` (2021 AU7 series, 43") |
| Tizen | 6.0 |
| Build / profile | `21_KANTSU2E_UHD_BASIC` |
| Kernel | Linux 4.1.10, `armv7l` |
| .NET | Microsoft.NETCore.App **3.1** |
| Browser | Chromium 76.0.3809.146 |
| Root shell | `0.0.0.0:2323`, UID 0 |
| Control API | `127.0.0.1:2324` |
| Boot persistence | verified across a full power cycle |
| Dashboard / show | verified, including remote input and audio |

This is the only configuration the 1.1.0 release was tested on.

## Version table

| Tizen / platform | SamPwn status | What to expect |
|---|---|---|
| **6.0**, Samsung 2021 AU7 | **Verified** | Everything in this repository. |
| **6.0**, other Samsung models | **Compatible if the preflight passes** | The exploit's capability check must find an unpatched launcher and .NET Core 3.1. Model-specific firmware can still differ. |
| **5.5** | **Untested** | Some 5.5 builds may have .NET Core 3.1 and the same launcher; others do not. No SamPwn test result. |
| **6.5 / 7.0 / 8.0 / 9.0 / 10** | **Not supported** | Newer firmware is a mixed field: some community reports still get the archive-launcher route, while others reject the SDB package-name injection. SamPwn does not vendor the .NET 6 `common` payload and has no test result on these versions. Do not assume compatibility. |
| **4.0 / 5.0 and older** | **Not supported** | No validated .NET Core 3.1 path. |
| Non-Samsung Tizen | **Not supported** | Different platform services and package layout. |

The upstream `samsung-tv-root` project maintains a broader, evidence-linked
compatibility table for models and exact firmware builds. See
[`vendor/README.samsung-tv-root.md`](../vendor/README.samsung-tv-root.md) and
the [upstream repository](https://github.com/chris-ritsen/samsung-tv-root).
Upstream reports are not SamPwn test results and do not imply that this
repository's boot hook or widget will work on those TVs.

## Preflight

The installer refuses to continue when the root route is unavailable. You can
run the same check yourself before installing:

```bash
cd SamPwn
PYTHONPATH=vendor/samsung_tv_root/src \
python3 -m samsung_tv_root archive-root probe 192.168.1.93 \
  --runtime 3.1 \
  --callback-host 192.168.1.171 \
  --payload-directory "$PWD/vendor/payloads/common31"
```

This acquires root once and prints a compatibility assessment. It is not a
harmless read: it exercises the same injection and launcher as the installer.
Only run it on a TV you own.

The preflight checks:

* `sdb` sees the device and the injection timing probe succeeds;
* `/usr/sbin/sdbd-tarlauncher` has effective `cap_setuid` and `cap_setgid`;
* `/usr/bin/dotnet` has effective `cap_setgid` and `cap_sys_admin`;
* a supported .NET runtime is present;
* `/usr/bin/tar` supports `--to-command`.

### Common failure messages

| Message | Meaning |
|---|---|
| `dotnet lacks effective SETGID/SYS_ADMIN capabilities` | The TV's dotnet binary is not usable for this route, or a firmware update changed it. |
| `sdbd-tarlauncher lacks effective SETUID/SETGID capabilities` | The SDB launcher was patched or is missing. |
| `TV tar does not support --to-command` | The archive extraction primitive is unavailable. |
| `no supported Microsoft.NETCore.App runtime (3.1 or 6+)` | The TV has no compatible .NET runtime. |
| `no authenticated root callback` | The launcher ran but the TV could not reach the laptop callback address, or the firmware blocked the payload. Check `--host` and the firewall. |
| `Unknown option --list-runtimes` | Runtime detection is unavailable; `--runtime 3.1` is required. |

## Firmware and updates

SamPwn depends on exact TV-side behavior, not on a firmware image. Do not
download or flash a firmware update as part of installing SamPwn.

A TV firmware update can:

* patch the SDB package-name injection;
* change the capabilities on `dotnet` or `sdbd-tarlauncher`;
* replace `parseinfo.sh` and remove the boot hook;
* change the widget install path.

After any firmware update, assume root is gone and re-run the installer. If the
preflight fails, there is no supported workaround in this repository.

## UEP and the boot hook

The tested TV applies a UEP (unsigned executable prevention) check to shell and
Node scripts started by systemd. A plain unsigned `.sh` file is blocked. The
boot hook works because:

* its shebang is `#!/usr/bin/gawk -f`, and the TV's gawk is not UEP-patched;
* it invokes commands through gawk's `system()`, which uses inline `sh -c`
  strings rather than executing an unsigned script file.

If a future firmware patches gawk, the boot hook stops working even if root
acquisition still succeeds. In that case the installer's temporary root shell
still works until reboot; SamPwn has no alternative persistence mechanism.

## Widget and browser requirements

The dashboard and show are ES5-compatible and target Chromium 76. They use:

* `XMLHttpRequest` polling (no WebSocket dependency);
* canvas 2D;
* HTML5 audio with MP3;
* `tizen.tvinputdevice.registerKey` for media and color keys;
* a bundled 11 KB Adwaita Mono subset because the TV has no monospace font
  (`fc-match monospace` returns a proportional Samsung font).

The widget package is `sampwnroot`; the app ID is `sampwnroot.dashboard`.
Tizen rejects package IDs containing dots and very short names, which is why
the package is not called `sampwn`.

## Reporting a new result

If you test another TV, record:

* exact model and regional suffix;
* Tizen version, build ID, kernel and architecture;
* `dotnet --list-runtimes` output;
* preflight result;
* whether the boot hook survived a full power cycle;
* whether the dashboard and `pwned.html` render and accept remote input.

Do not report a model as supported based only on a successful widget install.
Root and boot persistence are separate results.
