---
id: winmanager-windows
type: module-design
status: draft
title: Windows integration and recovery mechanisms
parent: winmanager-architecture
depends-on: [winmanager-core]
references: [winmanager-goals]
---

# Windows integration

## Responsibility and boundary

The adapter owns native discovery, notification normalization, HWND identity, physical effects, hotkeys, and recovery mechanisms.
`winmanager-architecture` owns its dependency boundary. Core decides layout and management policy.

A dedicated thread owns the native message loop and its hidden top-level notification window. Display broadcasts require that window form.
CsWin32-generated types and unsafe native operations remain inside this assembly.

## Observation and actions

Discovery inspects process identity, styles, ownership, visibility, DWM cloaking, visible-frame bounds, and monitor DPI.
Hard exclusions remove shell, child, application-hidden, cloaked, configured-ignore, and observer-process windows.
Semantic popup/tool/owned/no-activate candidates remain observable for Core policy and manual overrides.

Action-time identity checks protect against stale or reused HWNDs. Native placement accounts for invisible frame borders without activation or z-order changes.
Placement requests exact planned dimensions, including dimensions below native minimum hints. Applications can refuse those dimensions.
Focus requests respect Windows foreground restrictions and verify immediate observed focus.

Shortcut registration validates combinations and replaces registrations on the native thread.
Replacement releases the previous set before native registration. On conflict, it removes partial new registrations but does not restore the previous set.
This cleanup does not satisfy the transactional reload requirement in `winmanager-goals`.
The 64-entry shortcut signal queue drops new signals under saturation. This differs from public IPC admission, which can return `busy`.

## Recovery mechanism

The helper owns private per-user journals and acknowledged registration before hides. It checks host lifetime and protected window identity before restoration.
Restoration requires matching HWND, process identity, and a random window property. Uncertain identity does not authorize restoration.
Failed or uncertain restoration retains journal records for another attempt. Public client disconnection never substitutes for host death.

Graceful restoration concerns manager-hidden visibility and original show state, not pre-tiling rectangles.
Recovery journals always use `%LOCALAPPDATA%/WinManager/Recovery`. Host `--data-dir` does not relocate them.
A failed helper connection remains unavailable for that observer session.

## Evidence limits

Physical hotplug and mixed-DPI hardware behavior lack reproduced deployment validation. Native tests require the isolated session described in `winmanager-architecture`.

**Unverified risk:** final disposal can exceed the host shutdown budget because restoration and helper acknowledgments use their own waits.
Native AOT readiness remains unproven. Recovery commands and journals still use reflection-based generic JSON.
