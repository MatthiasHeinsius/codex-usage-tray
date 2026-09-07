# Codex Usage Tray

[![Build](https://github.com/MatthiasHeinsius/codex-usage-tray/actions/workflows/build.yml/badge.svg)](https://github.com/MatthiasHeinsius/codex-usage-tray/actions/workflows/build.yml)

Codex Usage Tray is a small Windows notification-area app that shows the usage attached to your signed-in Codex subscription.

This is an unofficial community project. It is not affiliated with or endorsed by OpenAI.

## What it shows

- Remaining 5-hour allowance and reset time
- Remaining weekly allowance and reset time
- Inference tokens used today
- Total inference tokens reported by the account
- Live countdowns in a compact view

The tray icon uses two rings. The inner ring shows the 5-hour allowance and the outer ring shows the weekly allowance. The app refreshes the usage figures and open-window countdowns every minute.

## Screenshots

| Extended view | Compact view |
| --- | --- |
| ![Extended Codex usage popup](docs/images/extended.png) | ![Compact Codex usage popup](docs/images/compact.png) |

## Requirements

- 64-bit Windows 10 or Windows 11
- Codex CLI installed and signed in with your ChatGPT account

The portable release includes the .NET desktop runtime. The target computer does not need a separate .NET installation or administrator access.

## Install

Download [`CodexUsageTray.exe`](https://github.com/MatthiasHeinsius/codex-usage-tray/releases/latest/download/CodexUsageTray.exe) from the [latest GitHub release](https://github.com/MatthiasHeinsius/codex-usage-tray/releases/latest), copy it to a permanent location, and run it. You can also build the same executable from source.

Left-click the tray icon to open or close the usage window. Right-click it to refresh, configure startup behavior, open the Codex usage page, or exit.

If you enable `Start with Windows`, keep the executable at the same path. The startup entry points to that file.

The executable is not code-signed. Windows SmartScreen or antivirus software may warn about a downloaded copy.

## Features

The popup has compact and extended views. Compact mode shows each allowance and its countdown. Extended mode adds reset timestamps, progress bars, and inference totals. The pin button keeps the popup open and above other windows.

By default, the app starts a new expired 5-hour or weekly window by sending an ephemeral `Hi` request with GPT-5.6 Luna. You can turn this off in the tray menu. The request consumes Codex inference. The app verifies the live reset time before sending and records completed triggers so it does not repeat them after a restart.

## Usage data and privacy

The app starts the installed Codex CLI in app-server mode and calls its read-only account methods. It does not read, copy, or store the credentials in `%USERPROFILE%\.codex\auth.json`.

The account service can publish daily token buckets one day late. If today's account bucket is unavailable, the app totals `token_usage_record` entries in this computer's local Codex session history. If the account total ends at yesterday, the app adds the local current-day total to the displayed inference total. No usage data leaves the computer except through the Codex CLI request described above.

The app stores its view and automation settings under `HKEY_CURRENT_USER\Software\CodexUsageTray`. The optional Windows startup entry uses `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run:

```powershell
dotnet build .\CodexUsageTray.slnx -c Release
dotnet run --project .\src\CodexUsageTray\CodexUsageTray.csproj -c Release --no-build -- --self-test
```

To create the self-contained Windows x64 executable, double-click `build-release.cmd` or run:

```powershell
dotnet publish .\src\CodexUsageTray\CodexUsageTray.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o .\artifacts\win-x64
```

If Codex is installed somewhere unusual, set `CODEX_USAGE_CODEX_PATH` to the full path of `codex.cmd` or `codex.exe` before starting the app.

The repository includes a screenshot generator for maintainers. It uses synthetic usage figures and never reads account data:

```powershell
dotnet run --project .\tools\CodexUsageTray.ScreenshotGenerator -- .\docs\images
```

## Current limitation

The account usage method is marked experimental by the Codex app-server. A future Codex CLI release may change it. If that happens, the app reports the error instead of inventing a usage figure.

## License

Codex Usage Tray is available under the [MIT License](LICENSE).
