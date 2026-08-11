# Codex Usage for Windows

A small, native Windows tray app for one job: keeping your OpenAI Codex usage visible.

It is intentionally Codex-only. There are no provider plugins, browser-cookie imports, API-key forms, or unrelated dashboards.

## Features

- Supersampled, anti-aliased system-tray meter with an original six-lobed contour showing the least available Codex quota window; a fresh window is `100` and fully filled.
- Compact popup with available percentages for the 5-hour and weekly windows, reset countdowns, plan, and credits when available.
- Persistent 24-hour, 7-day, 30-day, and 90-day history charts, with per-point hover details and markers for quota restoration or resets, opened from the tray's right-click menu.
- Support for model-specific Codex limits returned by the service.
- Manual refresh plus configurable 1, 2, 5, 15, or 30 minute polling.
- Optional Windows notifications when available quota drops to 30%, 20%, 10%, or 0%.
- System, dark, and light themes.
- Optional per-user launch at sign-in; no administrator access required.
- Single-instance behavior and a self-contained single-file build.

## How authentication works

Codex Usage reuses the ChatGPT sign-in already stored by Codex in `%USERPROFILE%\.codex\auth.json` (or `$env:CODEX_HOME\auth.json`). It never asks for or stores a password, API key, cookie, or copied token.

If the access token expires, the app uses the existing Codex refresh token and atomically updates the same auth file. It never writes credentials anywhere else and never logs token values. Network calls are limited to OpenAI's `chatgpt.com` usage service and `auth.openai.com` token service.

Use ChatGPT authentication rather than API-key authentication. OpenAI documents the supported Codex sign-in methods and the `codex login` flow in its [authentication guide](https://learn.chatgpt.com/docs/auth).

## Build and run

Prerequisites: Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet run --project .\src\CodexUsage.App\CodexUsage.App.csproj
```

To create a self-contained executable that does not require .NET on the destination computer:

```powershell
.\scripts\build.ps1
.\artifacts\win-x64\CodexUsage.exe
```

The build also writes `CodexUsage.exe.sha256` beside the executable.

For Windows on ARM:

```powershell
.\scripts\build.ps1 -Architecture arm64
```

To install it for the current user and create a Start Menu shortcut:

```powershell
.\scripts\install.ps1
```

The install script copies the app to `%LOCALAPPDATA%\Programs\CodexUsage`. Enable launch at sign-in from the tray menu or Settings.

## Test

The tests use no third-party packages:

```powershell
dotnet run --project .\tests\CodexUsage.Tests\CodexUsage.Tests.csproj
```

An opt-in live check reads the local Codex login, calls OpenAI once, and prints only the plan and quota percentages:

```powershell
dotnet run --project .\tests\CodexUsage.Tests\CodexUsage.Tests.csproj -- --live
```

## Privacy and limitations

- Settings are stored in `%LOCALAPPDATA%\CodexUsage\settings.json`; no secrets are stored there.
- Usage history is stored locally in `%LOCALAPPDATA%\CodexUsage\history.jsonl`. Flat duplicate readings are compressed, and history is limited to 90 days and 50,000 samples.
- This is an independent, unofficial utility and is not affiliated with or endorsed by OpenAI.
- Local release builds are unsigned, so Windows SmartScreen may show an unrecognized-app warning. Verify the adjacent SHA-256 file before running a distributed copy.
- The tray app follows the behavior of CodexBar's OAuth usage source. The underlying ChatGPT usage route is not a public, versioned API and may change; parser and endpoint updates may occasionally be necessary.
- If only API-key authentication is configured, the app asks you to sign in with ChatGPT because ChatGPT plan quotas are separate from API billing limits.

## Inspiration

Inspired by [CodexBar](https://github.com/steipete/CodexBar), which is MIT-licensed. This implementation is Windows-native and limited to Codex.
