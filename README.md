# TaskHarbor

**A calm, local-first task workspace for Windows.**

[![Windows build and test](https://github.com/Crazyer-Flove/TaskHarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/Crazyer-Flove/TaskHarbor/actions/workflows/ci.yml) · [GitHub repository](https://github.com/Crazyer-Flove/TaskHarbor) · [Report a bug](https://github.com/Crazyer-Flove/TaskHarbor/issues)

[简体中文](README.zh-CN.md) · [User guide](docs/USER_GUIDE.md) · [Development](docs/DEVELOPMENT.md) · [Verification](docs/VERIFICATION.md)

TaskHarbor combines an Eisenhower matrix with a desktop edge handle, quick capture, and an optional compact panel. The application UI is currently Simplified Chinese; its product name, repository and developer documentation use English.

## Features

- Four quadrants, drag-to-reclassify, search, and board/list views.
- Day, week and month planning **independent of optional deadlines**.
- A workbench, compact panel, and task editor that preserves board context.
- Subtasks, batch completion, scheduling for today, and recoverable deletion.
- Undo, a recycle bin, explicit save status, rollback and retry after failed writes.
- Completion-driven daily/weekly/monthly recurrence, including month-end dates.
- A system tray entry, `Ctrl+Shift+T` capture shortcut, configurable edge collapse and pinning.
- Opt-in task reminders while the app is running, quiet hours and snooze.
- Optional OpenAI-compatible text/image task extraction with editable previews; loopback services may omit an API key.
- Local JSON storage, atomic writes, task-only import/export and Windows current-user key encryption.
- Migration from the previous DeskTodo application without deleting the original files.

## Run

Use `TaskHarbor.exe` from a release package or build it locally. Exit the previous DeskTodo app before the first normal launch.

Requirements:

- Windows 10/11 **x64** with .NET Framework **4.6.2 or later**.
- Microsoft Edge WebView2 Runtime. The SDK bundled with the executable does not replace the installed Runtime.

Double-click the executable, then click its screen-edge handle or use the tray icon. `Ctrl+Shift+T` opens capture when the shortcut is available. No administrator rights are required for normal use. A valid Runtime installation and a writable user data directory are required.

The executable is unsigned; builds do not include an Authenticode signature. Review the source or build it yourself according to your environment's software policy.

## Build and test

Windows PowerShell 5.1+ and the Windows .NET Framework compiler are sufficient to build. Node.js **22+** is required only for JavaScript tests. No npm installation or frontend bundler is needed.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/restore.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

Or double-click `Build.cmd`. The first restore downloads the pinned **Microsoft.Web.WebView2 1.0.4191.47** SDK from official NuGet. Output is written to `dist/`; generated binaries and SDK caches are ignored by Git.

For an isolated demo:

```powershell
.\dist\TaskHarbor.exe --demo --data-dir .\build\demo
```

For a noninteractive diagnostic of the **actual release binary**:

```powershell
$p = Start-Process .\dist\TaskHarbor.exe -ArgumentList '--self-test --data-dir .\build\binary-check' -PassThru -Wait
Get-Content .\build\binary-check\self-test.json
```

This checks the embedded SDK, native loader, installed Runtime, resources and directory writes. It does **not** verify visual layout or user interactions.

## Data and privacy

The default directory is `%APPDATA%\TaskHarbor`. `--data-dir` selects an isolated directory and disables automatic legacy migration. A custom task storage path can be selected in settings.

Task data remains local unless you export it or explicitly submit text/images to your configured AI service. There is no telemetry, account system, cloud sync, or background AI submission. API keys are encrypted using Windows DPAPI for the current user; task exports do not include configuration or keys. Browser demonstration data is separate from desktop data.

Reminders need TaskHarbor running. Exiting stops reminders; Windows notification settings may suppress their display. The host checks approximately every ten seconds, catches up at most the last 24 hours, and respects configured quiet hours. System notifications omit private task titles.

## Repository layout

```text
src/          C# WinForms host, storage, AI transport and desktop services
web/          Local HTML/CSS UI, controller and independently tested domain model
assets/       Application icon
tests/        Domain, UI-controller and native tests with isolated fixtures
scripts/      SDK restore, build, test, packaging and source audit
docs/         User guide, development notes, verification and publishing guide
examples/     Fictional sample task data (never personal application data)
.github/      Windows CI and issue/pull-request templates
```

See [the verification report](docs/VERIFICATION.md) for what was actually tested and what still needs manual validation. Concept images are not presented as screenshots of the delivered app.

## Get the source

```powershell
git clone https://github.com/Crazyer-Flove/TaskHarbor.git
cd TaskHarbor
```

Fork the repository to contribute or maintain your own version. See [GitHub publishing](docs/PUBLISHING.md) for source publishing and release packaging. Windows CI produces a downloadable build artifact; it does not automatically create a GitHub Release.

## License

TaskHarbor source code and first-party assets are distributed under the [MIT License](LICENSE). Microsoft WebView2 SDK/Runtime remain under their own terms; see [third-party notices](THIRD_PARTY_NOTICES.md). The name is a product identifier for this project, not a claim of trademark registration or name exclusivity.
