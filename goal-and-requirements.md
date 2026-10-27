---
id: winmanager-goals
type: goal-and-requirements
status: draft
title: WinManager product and UX requirements
references: [winmanager-protocol]
---

# WinManager

## Goal and audience

WinManager is a .NET window manager for the owner's daily Windows desktop. It reduces repeated window arrangement and preserves useful work context.

Mouse actions and keyboard commands are equal ways to control the desktop. Neither is a secondary recovery path for the other.
GlazeWM supplies ideas, not a feature-parity target. Personal workflow needs determine scope.

## Status of this specification

The owner confirmed the product direction below during the workspace review. This document remains draft until the owner reviews the written specification.
These requirements describe intended behavior, not a claim that every capability exists. Module specs distinguish current behavior from implementation gaps.

## Product requirements

### Layout and direct manipulation

- Each per-monitor workspace owns its layout mode and arrangement.
- A workspace without a saved preference uses focused split-tree mode.
- In split-tree mode, a new normal window splits beside the focused tile, with the chosen split direction.
- Other intended modes are automatic columns or rows, master-and-stack, and a scrolling strip. Window insertion follows the selected mode, not one universal rule.
- In split-tree mode, a drop near a target edge inserts beside that tile. A center drop swaps the two windows.
- Mouse resize changes layout proportions. The arrangement retains deliberate proportions instead of automatic defaults.
- Automatic placement, direct manipulation, and keyboard commands must produce coherent results. Windows that require ordinary floating behavior remain usable.

Mode names define product concepts, not complete algorithms. Empty-workspace insertion, mode-switch conversion, scrolling navigation, and exact drop regions have no approved design yet.

### Workspaces and continuity

- Independent per-monitor workspaces are the supported baseline. A switch on one monitor does not switch the others.
- A normal host restart restores the working arrangement of surviving windows on a best-effort basis.
- That arrangement includes workspace membership, tile positions, split proportions, layout modes, and floating overrides. The manager does not launch missing applications.
- Normal exit restores manager-hidden visibility and show state. It leaves the latest arranged positions and sizes in place.

Window matching and conflict handling for restart restoration require a separate design. Current runtime GUIDs are not persistent application identities.

### Pause and configuration

- Pause suspends automatic placement and automatic workspace visibility enforcement. It does not disable deliberately issued commands.
- Resume reconciles current intent with the observed desktop.
- A configuration reload validates the complete candidate before application. Invalid configuration leaves the current configuration and session intact.
- A valid reload preserves the session. It does not restart the host or discard arrangement state.
- A lightweight companion provides a reload action. Its visual form and other controls have no approved design yet.

### Reliable desktop control

The manager must not strand windows after a workspace hide or host failure. It reports native failures instead of presumed success for geometry or focus.
External clients can query committed state, issue typed commands, and observe changes. The IPC contract lives in `winmanager-protocol`.

## Experiments, not settled UX

A desktop-wide collection can group the individual workspaces of multiple monitors. This experiment does not replace the independent baseline.
Collection membership, switching, and monitor-disconnect behavior have no approved design.

The owner expressed interest in a Fibonacci-style layout and a canvas-style layout. The owner did not approve interaction semantics for either layout.
Canvas use across multiple monitors remains open. Lightweight visual feedback is also experimental, not a commitment to a particular tray menu or overlay.

## Boundaries

The product targets Windows and the interactive user session. Exact GlazeWM protocol, configuration, CLI, and feature parity are not requirements.
The headless engine remains usable without a companion. Companion visuals must not define a second source of desktop state.

Windows foreground and privilege restrictions constrain native control. The product reports those limitations and does not bypass them.
Current architecture and safety boundaries live in `winmanager-architecture`. Historical Junie plans and parity backlogs do not override this document.
