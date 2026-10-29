# Vendored root tooling

This directory contains the parts of
[`chris-ritsen/samsung-tv-root`](https://github.com/chris-ritsen/samsung-tv-root)
needed to acquire root on the tested TV.

* `samsung_tv_root/` — Python package source (Unlicense)
* `payloads/common31/` — `SamsungTvArchiveRoot.dll` and
  `SamsungTvRootAgent.dll`, compiled from upstream 0.0.12
* `LICENSE.samsung-tv-root` — upstream Unlicense
* `README.samsung-tv-root.md` — upstream README for provenance and its broader
  compatibility table

## Local patch

`src/samsung_tv_root/archive_root.py` differs from upstream 0.0.12. The
launcher is detached from the `sdb` shell so `sdbd` is not blocked for the
whole root session. See
[`docs/THIRD_PARTY.md`](../docs/THIRD_PARTY.md#local-modification) for details.

The patch is limited to launcher process management and logging. The SDB
package-name injection and the TV-side payloads are upstream code.

## Why the release bundle is not vendored

The upstream 0.0.12 Linux release is a 77 MB PyInstaller bundle that includes a
Python runtime and all TV payload profiles. SamPwn needs only the
`common31` assemblies (about 27 KB) and runs the Python source directly, so the
release bundle and its build artifacts are intentionally absent.

The package requires Python 3.12+ and has no third-party runtime dependencies.
The host still needs the `openssl` command-line tool.
