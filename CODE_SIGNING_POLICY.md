# Code signing policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation

This policy covers the Windows files of **CCWrapper** that are published on GitHub Releases at <https://github.com/PieMH/CCWrapper>.
Effective from 17 October 2026.

> Until SignPath Foundation approves the project and a signed release is published, releases are **unsigned**. Windows SmartScreen or your antivirus may warn about them. This policy then describes what will be signed once approval is given.

## What is signed

| File | What it is |
|---|---|
| `CCWrapper.exe` | The app |
| `CCWrapper.dll` | The app's code |
| `CCWrapper.StatusRelay.exe` | The silent status-line relay (native) |
| `PieMH.CCWrapper-win-Setup.exe` | The installer (Velopack) |

SignPath signs the files of the publish folder that its artifact configuration covers (the three above and any other first-party binary built from this repository), before they are packed, and then signs `PieMH.CCWrapper-win-Setup.exe`. Velopack's own `Update.exe` and the rest of what it adds to `Portable.zip` stay as Velopack ships them.

Only files built from this repository are signed. Third-party files, such as the .NET runtime and other libraries, are not signed by this project (see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)).

## How a release is built

- Releases are built by **GitHub Actions** in this repository, from **tagged source**. They are not built on a personal computer.
- The signing request is sent to SignPath from that workflow. Only builds of a tagged release are submitted.
- An approver approves each signing request before the signature is applied (see roles).
- The source code is public, under the GPLv3, so anyone can compare a release with its tag.

## Roles

| Role | Who |
|---|---|
| Committers and reviewers | [PieMH](https://github.com/PieMH) |
| Approvers | [PieMH](https://github.com/PieMH) |

The project has one maintainer, who holds both roles. Pull requests from others are reviewed by the maintainer before they are merged. The `main` branch only takes pull requests.

## Privacy statement

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

The one automatic exception is the update check of an installed copy. It contacts GitHub, the app asks you before the first check, and you can turn it off in **About**. It sends only what any web request sends, such as your IP address and a user agent.

The full statement is in [PRIVACY.md](PRIVACY.md).

## Reporting a problem with a signed file

If you believe a signed file is malicious, or the signing process was misused, report it privately as described in [SECURITY.md](SECURITY.md).

## Attribution

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).
