---
id: winmanager-recovery
type: module-design
status: draft
title: Independent crash recovery executable
parent: winmanager-architecture
depends-on: [winmanager-windows]
---

# Recovery executable

## Responsibility and boundary

Recovery supplies a separate process that can survive host failure. Its entry point delegates to `WindowsRecovery` in the Windows assembly.
`winmanager-windows` owns journal, acknowledgment, identity, and restoration mechanisms. This executable must not duplicate those contracts.

It is not a second layout manager, a public command server, or a Windows service.
It monitors the exact host process lifetime and restores only registered, identity-matched windows.
A helper pipe disconnect alone does not trigger restoration while the host lives.

## Deployment

Host deployment must include the helper executable and its framework-dependent files. An unavailable helper prevents safe workspace hides.
A standalone executable does not imply Native AOT. Current deployment still requires the .NET runtime and the Windows/Core/Contracts assemblies.
