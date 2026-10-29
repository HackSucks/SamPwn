# SamPwn

**A persistent root toolkit and dashboard for Samsung Tizen TVs.**

SamPwn turns a rooted Samsung Tizen TV into a more accessible playground for experimentation. It installs a small background service, provides an on-demand root shell over your local network, and adds a TV-remote-friendly dashboard with device controls and a one-time ASCII-art show.

Built for **your own TV, on your own network**. SamPwn is not a remote exploit and does not bypass Developer Mode.

## ✨ Features

- **Persistent across reboots** — Start the SamPwn services automatically when the TV boots, without needing your laptop connected.
- **On-TV dashboard** — Check service status, toggle the root shell, trigger the show, or exit back to the TV.
- **Network root shell** — Connect to the TV as root over TCP port `2323`. Enable or disable access from the dashboard without rebooting.
- **Local-only control API** — The dashboard communicates with the service through `127.0.0.1:2324`; the control API is not exposed to the LAN.
- **One-time startup show** — Display the bundled `pwned.html` show on the first launch after boot.
- **Simple installation and removal** — Includes an installer, an uninstaller, SHA-256 file verification, and a backup of the original boot hook.

## ⚠️ Security first

> [!WARNING]
> **The root shell is unauthenticated and runs commands as root.**
>
> Anyone who can connect to your TV on TCP port `2323` can execute commands with root privileges. There is no password or authentication layer.

Keep your TV on a trusted network, disable the shell whenever you aren't using it, and **never expose port `2323` to the internet**.

Read [docs/SECURITY.md](docs/SECURITY.md) before installing SamPwn.

## 📺 Compatibility

SamPwn is currently verified on a specific Samsung Tizen 6.0 TV. Compatibility with other models depends on the available system capabilities and root-acquisition path.

| Platform | Status |
|---|---|
| Samsung Tizen 6.0 — 2021 AU7 series | ✅ Verified on the GU43AU7179UXZG |
| Other Samsung Tizen 6.0 TVs | ⚠️ Conditional — run the compatibility probe first |
| Samsung Tizen 5.5 | 🧪 Untested |
| Samsung Tizen 6.5, 7.0, 8.0 and newer | ❌ Not currently supported |
| Samsung Tizen 4.0, 5.0 and older | ❌ Not supported |
| Non-Samsung Tizen TVs | ❌ Not supported |

The verified test device runs build `21_KANTSU2E_UHD_BASIC`, with a 4.1.10 ARMv7 kernel, .NET Core 3.1, and Chromium 76. Root acquisition, persistence, the dashboard, the show, and a full power-cycle reboot have been tested.

Other Tizen 6.0 devices need a compatible, unpatched `sdbd-tarlauncher`, the required capabilities, .NET Core 3.1 support, and a compatible `tar` implementation. Passing the probe is a compatibility check, not a guarantee.

Newer Tizen versions may still expose parts of the required functionality, but SamPwn only includes the `.NET Core 3.1` `common31` payload and has not been validated on those versions.

See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the compatibility probe, requirements, and failure messages.

## 🚀 Getting started

### Requirements

**On your computer:**
- Linux with Bash, `curl`, `openssl`, `sha256sum`, and Python 3.12 or newer.
- [Tizen Studio](https://developer.tizen.org/development/tizen-studio/download), with `sdb` and `tizen` available on your `PATH`. Alternatively, configure `TIZEN_STUDIO`.
- Network connectivity to the TV.
- A firewall configuration that allows the TV to reach the installer’s HTTP and callback ports.

**On your TV:**
- Developer Mode enabled.
- Your computer's IP address added to the Developer Mode host list.
- The SDB authorization prompt accepted.

### Install

Clone the repository and run the installer with your TV's IP address:

```bash
git clone https://github.com/HackSucks/SamPwn.git
cd SamPwn
./install.sh --tv 192.168.1.93
```

Replace `192.168.1.93` with your TV's actual IP address.

The installer handles the setup for you:

1. Connects to the TV through SDB.
2. Installs the signed `SamPwn.wgt` dashboard.
3. Starts a temporary HTTP server to transfer the required files.
4. Acquires root and verifies downloaded files against their SHA-256 hashes.
5. Backs up the original boot hook.
6. Installs the persistent startup hook and starts the SamPwn services.
7. Launches the dashboard on the TV.

### Installer options

| Option | Description |
|---|---|
| `--tv IP` | TV address; can also be set through `TV_IP` |
| `--host IP` | Computer's IP address reachable from the TV; auto-detected if omitted |
| `--port N` | Preferred HTTP port; defaults to `8000`, with a free port chosen if necessary |
| `--shell on\|off` | Choose the initial root-shell state; defaults to `on` |
| `--no-show` | Disable the automatic one-time show |

## 🎮 Using SamPwn

Once the installation finishes, open the **SamPwn** app on your TV.

The dashboard lets you check the service status, enable or disable the network root shell, manually trigger the show, and return to the TV interface.

### Managing the root shell

The root shell is enabled by default so the installer can verify that everything is working.

When you're finished, disable it from the dashboard:

**Dashboard → Root shell → Turn root shell OFF**

The selected state is saved in:

```text
/opt/etc/sampwn/shell.state
```

SamPwn applies this setting again at boot, so disabling the shell persists across reboots.

### Emergency kill switch

To prevent the SamPwn boot hook from starting its payloads on the next boot:

```bash
touch /opt/etc/sampwn/disabled
```

This is useful when you want to temporarily stop SamPwn from starting automatically. It is not a substitute for disabling the network shell before leaving the TV connected to an untrusted network.

## ✅ Verify the installation

From your computer, run a command through the root shell:

```bash
python3 tools/rootshell.py --host 192.168.1.93 'id'
```

A successful response should resemble:

```text
uid=0(root) gid=0(root) context="User"
```

You can also check the local control API through the root shell:

```bash
python3 tools/rootshell.py --host 192.168.1.93 \
  'curl -fsS http://127.0.0.1:2324/status'
```

When the installation is healthy and the shell is enabled, the status response should report values such as `"shell":true`, `"hook":true`, and `"widget":true`.

To test persistence, fully power-cycle the TV rather than simply putting it in standby. Wait for the dashboard to appear and repeat the root-shell test.

## 🗑️ Uninstalling

To remove SamPwn:

```bash
./uninstall.sh --tv 192.168.1.93
```

The uninstaller stops the services and shell, removes the installed widget, and restores the original `/opt/etc/parseinfo.sh` from its backup.

Additional options:

| Option | Description |
|---|---|
| `--keep-data` | Preserve `/opt/etc/sampwn` |
| `--keep-widget` | Leave the SamPwn widget installed |

See [docs/UNINSTALL.md](docs/UNINSTALL.md) for further details.

## 🔧 How it works

SamPwn consists of a few small components that work together:

- **Root acquisition:** Uses a patched, vendored copy of [`samsung-tv-root`](https://github.com/chris-ritsen/samsung-tv-root) to acquire root through the supported device foothold.
- **Boot persistence:** Uses a modified `/opt/etc/parseinfo.sh` hook to start SamPwn automatically.
- **Background service:** Runs the .NET Core 3.1 daemon as root, exposing the local control API and managing the network shell.
- **Dashboard:** A Tizen widget that communicates with the daemon and accepts input from the TV remote.
- **Transient service units:** Creates systemd units under `/run/systemd/system/` during boot, avoiding stale unit files across reboots.

After installation, SamPwn does not depend on `sdb shell`, `sdb root on`, or `sdb push` to run. SDB is used for device discovery and the initial installation.

For the implementation details, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## 🛠️ Building from source

### Build the daemon

The daemon is written in C# and targets .NET Core 3.1. Use a .NET SDK capable of targeting `netcoreapp3.1`:

```bash
dotnet build sampwn-srv/SampwnSrv.csproj -c Release
```

Copy the generated files into the distribution directory:

```bash
cp sampwn-srv/bin/Release/netcoreapp3.1/SampwnSrv.{dll,deps.json,runtimeconfig.json} \
  dist/srv/
```

### Build the dashboard

The Tizen widget is built with Tizen Studio and a configured TV signing profile:

```bash
cd app
tizen package -t wgt -s tv -- .
```

Install the resulting `SamPwn.wgt` when testing your new build.

For more information, see [docs/INSTALL.md](docs/INSTALL.md#building).

## 📁 Repository structure

```text
app/                  Dashboard sources, show assets, font and SamPwn.wgt
sampwn-srv/           C# daemon source (.NET Core 3.1)
dist/srv/             Prebuilt daemon used by the installer
boothook/parseinfo.sh Boot persistence hook
tools/                Root-shell client and HTTP server utilities
vendor/               Patched root-acquisition source, payloads and licenses
docs/                 Installation, compatibility, security and other guides
install.sh            Installer
uninstall.sh          Uninstaller
SHA256SUMS            SHA-256 hashes for committed release files
```

## 🙌 Credits

- **Root acquisition:** [`samsung-tv-root`](https://github.com/chris-ritsen/samsung-tv-root), by its respective authors. Released under the Unlicense; SamPwn vendors a patched copy.
- **Font:** Adwaita Mono, copyright 2015–2026 Renzhi Li, licensed under the SIL Open Font License 1.1. SamPwn bundles a printable-ASCII subset as `app/sampwn-mono.ttf`.
- **Show audio:** `app/pwned.mp3` is part of the project's show assets. Check your redistribution rights before publishing a fork.

See [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md) for third-party notices.

## 📄 License

SamPwn is licensed under the [MIT License](LICENSE). Vendored components retain their respective licenses.

**SamPwn is an independent project and is not affiliated with Samsung.**

