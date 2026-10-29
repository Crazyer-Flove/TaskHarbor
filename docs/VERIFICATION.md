# Verification — TaskHarbor

## 3.0.3 — 2026-10-29 (Asia/Shanghai)

- The idle handle uses the selected monitor's top-right working-area anchor with a DPI-scaled 24 logical-pixel inset, clamped for small screens. The expanded panel uses the same anchor.
- Existing saved coordinates select the monitor but do not override the right alignment.
- The 105-check regression suite and release-binary self-test passed; actual placement and animation remain subject to desktop/multi-monitor acceptance.

## 3.0.2 — 2026-10-29 (Asia/Shanghai)

- 105 checks passed: 39 domain/static, 13 UI-controller simulation and 53 native checks.
- 11 new checks cover top-edge anchoring, negative monitor coordinates, small work areas, exact motion endpoints, clamping, monotonic easing and reversal continuity.
- Motion retains a constant WebView size, translates its content and clips the host region on a 15 ms timer; no per-frame bitmap scaling or alpha-mode switching occurs.
- This is numerical/policy verification, not a real desktop animation acceptance result. Frame pacing, rendered clipping, focus, dragging and multi-monitor/DPI behavior still require hands-on verification.

## 3.0.1 — 2026-10-29 (Asia/Shanghai)

- 94 automated checks passed: 39 domain/static, 13 simulated UI-controller and 42 native checks.
- Eight new native checks cover transparent-corner hover entry, stationary-pointer suppression, exit/re-entry, manual-collapse rearming, held-button/drag suppression, release, the handle-to-panel gap and workspace exit.
- The native host polls hover entry including alpha-zero pixels using the existing 110 ms timer. Mouse exit retains the configured delay and pin/edit/dialog/AI protections.
- Geometry/state-policy tests do not verify real pointer movement or native window behavior. Interactive hover, multi-monitor/DPI transitions and drag positioning still require manual verification.

## 3.0.0 baseline — 2026-10-28 (Asia/Shanghai)

## Completed

- **39 domain/static checks**: legacy compatibility, independent deadlines, scope rules, local-day statistics, corrupt input, month-end/leap-year recurrence and UI identifier consistency.
- **13 UI-controller checks**: actual controller running in a DOM test double, including failed-save rollback, draft retention, retry, subtasks, recurrence/undo, recycle-bin restoration, search, batch completion and blocked writes after a failed load.
- **34 native checks**: actual C# storage/AI/reminder code, isolated migration, backups, atomic replacement, encrypted keys and reminder timing/snooze.
- SDK restored afresh from official NuGet at pinned version 1.0.4191.47.
- The generated source ZIP was extracted into an empty directory, restored its own SDK cache, built successfully and passed all 86 checks again. This caught and fixed a first-restore exit-code handling issue.
- Windows x64 executable compiled at version 3.0.0.0, with embedded application resources and icon.
- The actual executable's noninteractive self-test passed: native loader loaded; installed WebView2 Runtime detected as 155.0.4283.62; all embedded UI files extracted; isolated-directory writes succeeded.
- Hosted Windows CI passed for the initial source publication: [run 40647826123](https://github.com/Crazyer-Flove/TaskHarbor/actions/runs/40647826123), commit `15d63c8c57e20023a0af4e9cf4b12a5da4d080ba`. SDK restore, source audit, all test stages, executable build, packaging and artifact upload completed successfully. The README badge reports later runs separately.

All 86 automated checks passed. Reproduce using `scripts/test.ps1`; fixtures are created under `build/`. These tests do not read or mutate personal task/configuration directories.

## Not yet verified

The desktop-control tool's interactive launch approval timed out. No valid screenshot of the delivered application was captured, so **desktop visual acceptance remains blocked**. Do not treat a domain check, DOM simulation, compilation or runtime detection as a visual pass.

Still required before a stable public announcement:

- Actual workbench, compact panel and side-editor layout at 100%, 125% and 150% DPI.
- Empty, 8-task and 20-task boards; many tasks in one quadrant and long titles.
- Native focus, real drag/drop, context menus and date/time controls.
- Edge movement, repeated collapse/expand, and multiple monitors.
- Real Windows notification presentation, quiet hours and resume from sleep.
- Keyboard/screen-reader behavior and final rendered text/target contrast.
- A real user-selected AI endpoint; no paid or personal AI request was made during tests.

No team collaboration, cloud sync, account service or closed-application reminder scheduler is implemented. Those were explicitly deferred product directions, not part of this local desktop release.

See `design-qa.md` for the blocking visual QA status. The repository is suitable for source review and independent building; its desktop behavior still requires the checks above.

## README illustrations

The bilingual images in `docs/images/` are documentation illustrations drawn for the README, with fictional task text. They explain the quadrant layout and the distinction between plans and deadlines. They are not captured application screenshots, do not demonstrate an English application interface, and do not change the desktop visual acceptance status above.
