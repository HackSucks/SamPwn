# Security policy

SamPwn installs a persistent root service and an unauthenticated LAN root
shell. Read [docs/SECURITY.md](docs/SECURITY.md) before installing it.

## Reporting a vulnerability in SamPwn

Please do **not** open a public issue for a vulnerability in SamPwn itself.
Use the repository's private security advisory feature (GitHub: **Security →
Advisories → New draft security advisory**) and include:

* SamPwn version (`VERSION`);
* TV model, Tizen version, and build;
* steps to reproduce;
* impact and any suggested fix.

Issues in Samsung firmware or the upstream `samsung-tv-root` project should be
reported to their respective maintainers.

## Scope

In scope:

* the SamPwn daemon, dashboard, installer, uninstaller, and boot hook;
* the local control API and shell state handling;
* privilege boundaries that SamPwn is expected to enforce.

Out of scope:

* the documented, intentional unauthenticated root shell on port 2323;
* attacks that require physical access to the TV;
* attacks that require the ability to install arbitrary Tizen apps;
* Samsung firmware vulnerabilities that are not caused by SamPwn;
* the upstream exploit's ability to obtain root on a vulnerable TV.
