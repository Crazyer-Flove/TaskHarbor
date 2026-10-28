# Development

TaskHarbor uses C# 5 / .NET Framework 4.6.2+, Windows Forms, and WebView2. It deliberately has no React, npm build or backend server dependency.

## Architecture

- `Program.cs`: single-instance entry, isolated data directories and noninteractive diagnostics.
- `HostForm.cs`: WebView2 bridge and stable panel/edge-handle rendering modes.
- `Native.cs`: Win32 geometry and per-pixel rendering. Do not mix whole-window alpha APIs with the pixel-handle mode.
- `Store.cs`: local JSON, validation, atomic replacement, DPAPI, migration, import snapshots and optional user-requested shell integration.
- `DesktopServices.cs`: tray and host timer; no dependence on hidden WebView timers.
- `AiClient.cs`: user-configured compatible API, SSE and editable structured output.
- `web/model.js`: deterministic task rules with no DOM, network or filesystem access.
- `web/panel.js`: controller, request correlation, optimistic update/rollback, undo and draft handling.

## Data schema

Tasks remain a JSON array. Each task keeps an `id`, `title`, `quadrant`, `period`, `planDate`, optional `date`/`due`, `done`, `doneAt`, `repeat`, `subtasks` and `schemaVersion: 3`. A deleted task has `deletedAt`. Existing unknown properties survive normalization.

`planDate` is the calendar anchor for planning. `date` remains the explicit deadline for compatibility with legacy exports. A blank deadline means no hard deadline. `doneAt` is an ISO instant; statistics convert it to the local calendar day. Recurrence records `generatedFrom` to avoid duplicate successor creation and preserves month-day anchors through short months.

Keep schema migrations lossless. In particular, do not reinterpret existing saved dates as disposable defaults.

## Tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
```

Tests evaluate actual JavaScript and actual C# source against isolated fixtures under `build/`. UI-controller tests use a test double and do not claim layout, browser event or accessibility compliance. Native tests do not create Forms or notification icons. See [verification](VERIFICATION.md).

## Packaging

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

The release executable embeds its UI and SDK wrappers. The native loader may also be extracted beside the executable; runtime files otherwise live under the selected application data directory. Do not commit restored DLLs or a Runtime installation.

The source package is an explicit allowlist. Run `scripts/audit-source.cjs` before publishing. This is a useful guard, not a guarantee that every possible secret pattern can be detected.
