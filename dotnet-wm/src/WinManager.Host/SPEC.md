---
id: winmanager-host
type: module-design
status: draft
title: Host composition and local IPC service
parent: winmanager-architecture
depends-on: [winmanager-windows, winmanager-recovery]
references: [winmanager-goals, winmanager-protocol]
---

# Host and local IPC service

## Responsibility and boundary

Host composes configuration, logging, the engine, the Windows adapter, shortcuts, and the public pipe server.
It runs as a normal-privilege WinExe in the interactive user session. It rejects Session 0.
Host does not calculate layout or perform direct native window actions. `winmanager-architecture` owns project edges.

Configuration validation precedes native initialization. The host waits for engine readiness before shortcut registration.
The pipe service transports validated requests to the engine. `winmanager-protocol` owns security, framing, output bounds, and subscription consistency.

## Configuration and lifetime

Generated configuration provides three named workspaces, eight-DIP inner and outer gaps, and no shortcuts.
Strict JSON validation rejects unknown or duplicate properties, invalid commands, and unsafe resource bounds.
Current configuration changes require host restart. No reload command or live configuration transaction exists.
The reload requirement in `winmanager-goals` therefore represents an implementation gap, not current behavior.

The instance lease covers the user/session even with a custom pipe name. Duplicate startup exits with code 5.
Startup or lifetime failure exits with code 2. Incomplete shutdown restoration exits with code 6.
The pipe service waits for engine shutdown before final output draining. Final event delivery to dead or slow clients is not guaranteed.

## Deployment and limits

The build copies the recovery helper beside the host. Framework-dependent deployment requires the complete publish directory and the .NET 10 runtime.
Host configuration still uses reflection-based JSON. Whole-host AOT publishing is not an established deployment contract.

**Unverified risk:** a connected same-user client can retain a server slot without a first-frame or idle-read timeout.
Same-user authorization does not isolate the host from hostile processes under that same identity.
