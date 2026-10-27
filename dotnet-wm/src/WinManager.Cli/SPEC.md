---
id: winmanager-cli
type: module-design
status: draft
title: Terminal controls and startup registration
parent: winmanager-architecture
depends-on: [winmanager-client]
references: [winmanager-goals]
---

# Terminal controls

## Responsibility and boundary

CLI exposes public SDK operations without engine or native adapter dependencies.
It provides snapshots, JSON commands, event watch, a terminal status display, an interactive menu, and explicit startup registration.
The menu is a developer control surface. It is not the experimental visual companion from `winmanager-goals`.

## Observable behavior

JSON commands are data, not shell programs. Standard output carries results and standard error carries structured errors.
Watch supports optional reconnection through the SDK. Status displays full-snapshot state, not inferred next-split intent.
The host does not currently expose that intent, so status reports configuration direction as unavailable.

Startup registration writes only the current user's WinManager Run value. It neither starts the host nor requests elevation.
Tests use disposable registration names and remove them. Ordinary verification must not alter the production startup entry.
The current implementation limits the complete Run command to 260 characters.

## Known test limitation

`StartupRegistrationTests.InstallUpdateAndIdempotentUninstallAffectOnlyDisposableValue` depends on temporary host and config paths.
Long paths can exceed the enforced Run-command limit before registry assertions execute. The fixture therefore does not pass in every workspace environment.
This constraint does not establish a registry or layout failure. The fixture and runtime restriction require separate review before repair.
