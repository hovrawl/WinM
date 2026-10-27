---
id: winmanager-architecture
type: architecture-design
status: draft
title: WinManager architecture and boundaries
parent: winmanager-goals
references: [winmanager-protocol]
---

# Architecture

## Authority and evidence

`winmanager-goals` owns product intent. This document records the current architecture and its safety constraints.
The project contains seven production projects under `dotnet-wm/src`, with tests under `dotnet-wm/tests`.

The boundaries below follow source inspection. Their original rationale is unconfirmed where this document explicitly identifies an inference.
All new specs remain draft until owner review. The imported IPC document is `winmanager-protocol`.

## Topology and dependency rules

The host composes one manager engine and one Windows adapter. The recovery executable is a narrow safety process, not another manager.

Direct production project references are:

- Core → Contracts.
- Windows → Core.
- Host → Windows, plus a build/deployment reference to Recovery.
- Recovery → Windows.
- Client → Contracts.
- CLI → Client.

Contracts has no project dependency. Transitive references make public DTOs available to the host and native adapter.
Host also uses Microsoft.Extensions.Hosting. Windows owns the private CsWin32 build dependency.
The Client validation executable is a development harness, not an additional runtime service.

Core must not import generated Win32 types, registry APIs, or native hooks. Client and CLI must not reach engine internals.
The Windows adapter owns mechanisms and native identity. Core owns desktop intent and layout policy. Host owns composition, admission, and lifetime.
Recovery mechanisms currently share the Windows assembly rather than a separate recovery library.

**Inferred rationale, unconfirmed:** these boundaries isolate native behavior for deterministic tests and permit external control without a UI dependency.

## State and effect flow

Native notifications, shortcuts, and IPC requests converge on the engine's bounded single-reader channel.
Only that serialized path mutates workspace assignments, layout trees, window-mode overrides, logical selection, and committed revisions.

The Windows adapter owns a dedicated native message thread, callbacks, hotkeys, and a hidden top-level notification window.
Native callbacks signal work. They do not wait for IPC clients. Periodic reconciliation closes notification and discovery gaps.

Core computes requested placements, focus, and visibility through `IWindowPlatform`. It captures the desktop again after native actions.
Committed snapshots report observed bounds and focus alongside logical assignments and layout trees. Command success does not guarantee an application obeyed asynchronous placement.

No external client owns authoritative desktop state. A visual companion must use public control and observation contracts.
Its UI framework, executable packaging, and interaction design remain undecided.

## Safety invariants

- Native handles never serve as public IDs. Action-time identity checks protect against HWND reuse.
- The recovery helper durably acknowledges protection before the manager hides a window.
- Helper unavailability prevents unsafe hides. Observation and positioning can remain available.
- Crash recovery restores only matching protected windows. A client disconnect does not establish host death.
- Public IPC accepts only verified local clients from the same user and Windows session. `winmanager-protocol` owns the exact wire/security contract.
- Slow event consumers disconnect rather than block state processing or silently lose transitions.
- One host owns each user/session, independent of custom public pipe names.

**Inferred rationale, unconfirmed:** normal privilege, bounded admission, and fail-closed recovery favor desktop safety over forced control.

## Implementation boundary

The source currently implements split-tree layouts and independent workspaces only. Product intent does not imply that other layout modes already exist.
`winmanager-core` owns layout, pause, interaction, and persistence gaps. `winmanager-host` owns configuration reload gaps.

The runtime targets .NET 10. Windows executables require Windows 10 build 15063 or newer.
Framework-dependent deployment is the documented baseline. Source-generated protocol JSON does not establish whole-application Native AOT support.

## Validation boundary

Core tests use fake native effects. IPC tests exercise real local transport and can use disposable HKCU startup entries.
Desktop tests require a disposable, isolated interactive session. The environment opt-in asserts isolation but does not create it.
Never launch the production host or interactive tests on an ordinary working desktop as part of routine verification.

Physical mixed-DPI/hotplug, actual logon execution, and cross-user/session/remote negative IPC checks lack reproduced deployment evidence.
The older counts in `dotnet-wm/docs/validation.md` are historical results, not current verification.
