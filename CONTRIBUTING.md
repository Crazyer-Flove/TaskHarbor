# Contributing

Build and test using the commands in [README](README.md). Keep UI work in `web/`, domain logic in `web/model.js`, and operating-system integration in `src/`.

Before proposing a change:

1. Run `scripts/test.ps1` and `scripts/build.ps1` on Windows x64.
2. Test with `--demo --data-dir` in an isolated directory, never personal task data.
3. Preserve old deadlines and unknown JSON fields when changing the schema.
4. Exercise write failures, keyboard navigation, compact mode and non-default DPI when relevant.
5. Never commit task exports, configuration files, API keys, SDK binaries or runtime profiles.

Visual changes should include screenshots with fictional tasks and a description of the viewport/DPI used. Automated DOM tests are not visual verification. Keep the native layered-window transition rules documented in `src/Native.cs` intact.

Open a pull request describing the problem, resulting behavior, tests and remaining limitations. No contributor agreement is required beyond the repository license.
