# Iteration two: ordered follow-up backlog

These are separate follow-up tasks, not implemented MVP functionality. Each task
must add its command contracts, events, client support, and automated tests.
This is functional Windows parity, not GlazeWM wire/config compatibility.

1. **Advanced layouts and interaction:** container selection, filtered focus
   cycling, absolute/percentage sizing and placement, border adjustment,
   drag-to-reorder/resize, advanced tiling direction. Reference
   `glazewm/packages/wm/src/commands` and `wm-common/src/app_command.rs`.
2. **Complete workspace behavior:** previous/recent/next-active navigation,
   refocus toggling, keep-alive workspaces, persistent bindings and hotplug
   migration, whole-workspace movement, display names, live workspace updates.
   Reference `wm_state.rs`, workspace/monitor commands, README workspace config.
3. **Full window states and effects:** floating placement/centering/topmost,
   fullscreen/maximized/minimized commands, title bars, opacity, focus borders,
   corners, taskbar behavior, and restoration of every modified native property.
   Gate DWM effects by OS capability. Reference `platform_sync.rs`,
   `app_command.rs`, and README window behavior/effects sections.
4. **Rules, configuration, and keyboard parity:** process/class/title equals
   and regex rules, triggers/run-once, multiple commands per binding, binding
   modes, extended layouts/chords, transactional reload/rollback, default window
   states, percentage gaps, optional GlazeWM YAML import. Reference
   `manage_window.rs`, `wm-common/src/parsed_config`, and README configuration.
5. **Automation and pointer behavior:** focus-follows-cursor, cursor jump
   triggers, startup/shutdown/reload hooks, opt-in shell execution with an
   explicit security model. Reference `packages/wm/src/main.rs` listeners and
   general commands. The MVP never executes arbitrary shell commands.
6. **Distribution and ecosystem integration:** signed installer, upgrades,
   uninstall cleanup, package-manager delivery, optional tray client, optional
   authenticated WebSocket adapter. Evolve the event catalogue, capability
   negotiation, and migration policy alongside new features.

The status-bar widget is a separate project. Zebar bundling, macOS support, and
exact GlazeWM CLI/IPC compatibility are excluded. Recheck the cloned inventory
before claiming this backlog delivers complete functional parity.

## Implementation progress

Features are implemented and committed individually for rollback. The ordered
backlog above remains the scope, not a claim of completed parity.

- Advanced layouts: **container selection complete**. Active-tree node targeting,
  parent/child/sibling/root navigation, snapshot selection and `selection.changed`
  events, selection-aware split orientation, typed SDK/CLI support, shortcut
  validation, and automated core/IPC tests. Selection is cleared when focus or
  tree/workspace context changes. Group movement/resizing is not yet implemented.
- Remaining advanced-layout features and tasks 2–6 are pending.