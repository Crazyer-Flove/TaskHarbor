# Changelog

## 3.0.3 — 2026-10-29

- Keep the idle handle near the top-right of the selected monitor, with a 24 logical-pixel right inset; previous horizontal positions no longer move it away from that corner.
- Expand the workspace below the same right-side anchor and retain the slide animation and hover behavior.

## 3.0.2 — 2026-10-29

- Anchor both the collapsed handle and expanded workspace to the current monitor's top working-area edge; dragging adjusts horizontal position and monitor selection.
- Slide down to open (280 ms) and slide up to close (240 ms), with cubic easing and elapsed-time-based frames.
- Preserve a constant WebView viewport and clip the reveal region; switch layered rendering modes only at transition boundaries.
- Reverse automatic closing from the current position when the pointer returns or editing/pinning begins.
- Added 11 geometry/easing regression checks; 105 automated checks pass. Actual desktop animation smoothness still needs manual verification.

## 3.0.1 — 2026-10-29

- Expand the workspace when the pointer enters the transparent edge-handle rectangle, without clicking; retain the configured mouse-leave collapse delay.
- Keep the handle-to-panel gap active, block expansion during dragging, and require pointer re-entry after manually collapsing over the handle.
- Added eight native hover-policy checks; desktop pointer/DPI behavior still requires manual acceptance.

## 3.0.0 — 2026-10-28

- Renamed the product, executable and source namespace to TaskHarbor.
- Rebuilt the workbench around persistent filters, optional statistics and quick capture.
- Added compact layout, contextual editing, subtasks and batch operations.
- Separated planning from deadlines; retained legacy deadline values during migration.
- Added recoverable deletion, undo, save rollback/retry, import snapshots and corrupt-file protection.
- Added completion-driven recurring tasks, local-date completion statistics and month-end handling.
- Added tray controls, a global capture shortcut and optional host-side reminders with quiet hours/snooze.
- Kept optional AI text/image extraction, added explicit data-destination disclosure and loopback no-key support.
- Added isolated tests, binary diagnostics, pinned SDK restore and GitHub-ready project documentation.

Desktop visual/DPI verification is tracked separately in `docs/VERIFICATION.md`.
