# Repository agent instructions

These instructions apply to the entire repository.

## Pull-request ownership

- Only the user may merge pull requests.
- Agents must never merge a pull request, enable auto-merge, or bypass the merge handoff.
- After a pull request is open and its required checks pass, report that it is ready and wait for the user to merge it.
- Do not push directly to `main`. Branch from the latest `origin/main`, keep the pull-request branch free of merge commits, and let the required CI checks finish.
- If later work depends on a pull request, continue only after the user confirms that they merged it; then fetch and fast-forward local `main` before proceeding.

## Fast project discovery

1. Run `git status --short --branch` and preserve unrelated or pre-existing changes.
2. Read `README.md`, `Directory.Build.props`, the relevant project file, and the matching CI/build script before editing.
3. Use `rg`/`rg --files` when available; on Windows without ripgrep, use `git grep`, `Get-ChildItem`, and `Select-String`.
4. Follow the execution path from `src/CodexUsage.App/Program.cs` into `TrayApplicationContext`, then into the relevant UI or Core service. Keep business logic in Core where it can be tested without paint code.
5. Look for an existing regression test in `tests/CodexUsage.Tests/Program.cs` before designing a new abstraction.

## Repository map

- `src/CodexUsage.Core/` contains reusable logic with no WinForms dependency:
  - `Authentication/`: reads and refreshes the existing Codex ChatGPT credentials.
  - `Networking/`, `Parsing/`, `Models/`, and `Services/`: fetch and normalize usage responses.
  - `History/`: history samples, persistence, reset detection, schedule inference, and forecasts.
  - `Tokens/`: best-effort local token-counter aggregation.
  - `Formatting/`: user-facing labels shared by the app.
- `src/CodexUsage.App/` is the .NET 10 Windows Forms tray application:
  - `TrayApplicationContext.cs` owns refresh, notifications, menus, and window lifetime.
  - `UI/PopupForm.cs` is the compact tray popup.
  - `UI/HistoryForm.cs` and `UI/UsageHistoryChart.cs` own history presentation.
  - `UI/PreviewRenderer.cs` renders deterministic documentation and QA images.
  - `Settings/` owns local settings and Windows startup integration.
- `tests/CodexUsage.Tests/` is a custom STA console test harness, not xUnit/NUnit/MSTest. Add the test method and invoke it from `Main`; the printed assertion count should increase.
- `scripts/build.ps1` creates single-file release binaries and adjacent SHA-256 files. `build.bat` and `build-small.bat` are wrappers.
- `screenshots/` contains tracked README images. `artifacts/`, `bin/`, and `obj/` are generated and ignored.
- `.github/workflows/ci.yml` defines the required linear-history, formatting, and Windows build/test checks.

## Core behavioral invariants

- Store and display quota as available percent. Clamp untrusted service values, preserve fractional values, and use reported window durations/reset times instead of assuming fixed primary/secondary window names.
- Use `DateTimeOffset` for instants and explicit `TimeZoneInfo` for local-hour behavior. Cover DST/time-zone-sensitive changes with deterministic zones where possible.
- History persistence is best effort: a damaged JSONL line or write failure must not prevent usage refresh. Preserve legacy sample deserialization when adding fields, 90-day retention, the sample cap, and flat-sample compaction.
- Forecast pace is based on consumption since the current reported reset. A learned schedule excludes observed off hours; until all 24 local hours have evidence, or when the schedule cannot explain current-window consumption, retain the wall-clock fallback.
- Equal quota readings around a long gap establish flat time. A long gap containing consumption is ambiguous and must not classify every intervening hour as active.
- Risky forecasts end at projected zero and show their lead time before reset. Safe forecasts end at the next reset and show projected quota remaining. Use `UsageDepletionForecast.ProjectionEndsAt` consistently for chart bounds, drawing, labels, and collapsed-axis calculations.
- Token totals parse counter events only. Do not read, log, test with, or expose conversation text.
- Network, file scanning, and token aggregation must not block the WinForms UI thread. Preserve cancellation and stale-result protection when changing background work.

## WinForms quality bar

- The app is Windows-only, targets `net10.0-windows`, uses `AutoScaleMode.Dpi`, and handles per-monitor DPI changes. Keep manual layout calculations valid at the minimum window size and non-100% DPI.
- Route all colors through `ThemePalette`; verify dark, light, and system themes for new controls or states.
- Update `LayoutContent`, theme application, visibility/enabled state, and disposal together when adding controls or owned resources.
- Keep the tray app single-instance and avoid activating or showing hidden preview/test forms on the user's desktop.
- For visual changes, render the relevant preview, inspect the PNG, and update the tracked screenshot when documentation would otherwise become stale. Useful commands after a Release build include:

```powershell
dotnet run --project .\src\CodexUsage.App\CodexUsage.App.csproj -c Release -- --render-preview .\artifacts\popup.png
dotnet run --project .\src\CodexUsage.App\CodexUsage.App.csproj -c Release -- --render-history .\artifacts\history.png
dotnet run --project .\src\CodexUsage.App\CodexUsage.App.csproj -c Release -- --render-icon=75 .\artifacts\icon.png
```

Use synthetic data in committed tests and previews. Local `%LOCALAPPDATA%\CodexUsage\history.jsonl` may help reproduce a user-reported bug, but never commit personal history or session data.

## Required verification

On a fresh checkout, restore first. Before handing off a code pull request, run the same gates as CI:

```powershell
dotnet restore CodexUsage.sln
dotnet format CodexUsage.sln --verify-no-changes --no-restore
dotnet build CodexUsage.sln -c Release --no-restore
dotnet run --project .\tests\CodexUsage.Tests\CodexUsage.Tests.csproj -c Release --no-build
```

- Treat warnings as failures; nullable and deterministic builds are enabled repository-wide.
- Run the smallest relevant check during iteration, then the complete sequence before push.
- The opt-in `--live` test reads the real local Codex login and performs a network call. Run it only when the user explicitly requests live verification; never include its credentials or raw response in output.
- After pushing, watch all required GitHub checks. Report failures with the failing job and cause; report success and wait for the user to merge.

## Release procedure

- The application version is in `src/CodexUsage.App/CodexUsage.App.csproj`. Version changes go through a pull request that only the user merges.
- Tag and package only from a clean, user-merged `main` that exactly matches `origin/main`. Never tag a release branch or an unmerged commit.
- Run the required verification, then build all four packages:

```powershell
.\scripts\build.ps1 -Architecture x64
.\scripts\build.ps1 -Architecture x64 -FrameworkDependent
.\scripts\build.ps1 -Architecture arm64
.\scripts\build.ps1 -Architecture arm64 -FrameworkDependent
```

- Publish self-contained and framework-dependent x64/ARM64 executables named `CodexUsage-vX.Y.Z-win-*.exe`, each adjacent `.sha256`, plus `SHA256SUMS.txt`.
- Recompute checksums after final renaming and verify every asset against them before tagging or upload.
- Use an annotated `vX.Y.Z` tag on the merged release commit and release title `Codex Usage vX.Y.Z`. Notes must distinguish self-contained downloads from builds requiring the matching .NET 10 Desktop Runtime and mention that binaries are unsigned.
- Verify the published tag target, asset names/sizes/digests, release status, and clean local/remote branch state before reporting completion.

## Security and privacy

- Never print, log, commit, or expose access tokens, refresh tokens, API keys, cookies, auth-file contents, or session conversation content.
- Authentication must continue to reuse Codex's ChatGPT login and atomically update only the existing auth file. Do not add alternate credential stores or broaden network destinations without explicit user direction.
- Settings must remain secret-free. Keep local history and token scans best effort and local-only.
