# Iteration two: completed feature record

The earlier follow-up backlog set broad Windows feature parity as its direction.
That backlog is retired. The current goal is one shared desk with transferable
workspace views. See [the project goal](project-goal.md) and
[the desk model migration plan](desk-model-plan.md).

This file records completed work. It does not set future scope.

- Advanced layouts: **container selection complete**. Features include active-tree
  node selection, parent/child/sibling/root navigation, snapshot selection,
  `selection.changed` events, selection-aware split orientation, typed SDK/CLI
  support, shortcut validation, and automated core/IPC tests. Selection clears
  when focus or tree/workspace context changes. Group move and resize are not
  implemented.
- Advanced layouts: **filtered focus cycle complete**. The manager wraps
  next/previous cycle actions within active workspaces. It supports filters for
  tiling/floating modes, active-workspace scope, deterministic tree and floating
  window order, observed `focus.changed` events, SDK/CLI and shortcut support,
  and automated tests for filters, boundaries, exclusions, and native failures.

These features remain part of the current implementation. Reuse or change them
only as needed for the desk model. No other item from the retired backlog is a
project commitment.

The status-bar widget is a separate project. The current external API and IPC
remain documented in [External commands API](external-api.md) and
[Local IPC protocol](protocol.md).
