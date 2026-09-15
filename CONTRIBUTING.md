# Contributing

Bug reports and focused pull requests are welcome. Use the repository's
[issue forms](https://github.com/MatthiasHeinsius/codex-usage-tray/issues/new/choose)
for bugs and feature requests. Report security problems privately as described
in [SECURITY.md](SECURITY.md).

## Development

Development requires 64-bit Windows and the .NET SDK version pinned in
[`global.json`](global.json). See [Build from source](README.md#build-from-source)
for the full build and release instructions.

Before opening a pull request, run:

```powershell
dotnet restore .\CodexUsageTray.slnx --locked-mode
dotnet build .\CodexUsageTray.slnx -c Release --no-restore
dotnet format .\CodexUsageTray.slnx --verify-no-changes --no-restore
dotnet test --solution .\CodexUsageTray.slnx -c Release --no-build --no-restore
```

Run `.\build-release.ps1` as well when changing publishing, packaging, or startup
behavior.

Keep each pull request limited to one change. Add or update tests when behavior
changes, and update the README or maintenance documentation when contributor or
user instructions change. Dependency changes must also update
`THIRD-PARTY-NOTICES.txt` and `docs/license-audit.md` when their license inventory
changes. See [repository maintenance](docs/repository-maintenance.md) for the
dependency and release procedures.
