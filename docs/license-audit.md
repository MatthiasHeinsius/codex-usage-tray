# Third-party license audit

Audit date: 2026-09-13. CI action inventory updated: 2026-09-15.

This report records the license sources needed to build the repository's third-party notice file. It separates software embedded in the released executable from tools used only to build or test the source. The upstream license and notice files linked below are the text to preserve. This report is not a substitute for those files.

## Repository evidence

The application project, `src/CodexUsageTray/CodexUsageTray.csproj`, has no `PackageReference` entries. `build-release.ps1`, also invoked by `build-release.cmd` and both CI workflows, publishes it for `win-x64` with `--self-contained true` and `PublishSingleFile=true`. The executable therefore embeds .NET runtime and Windows Desktop Framework binaries.

The test project directly references these packages:

| Package | Version | Role |
| --- | ---: | --- |
| `Microsoft.Testing.Extensions.CodeCoverage` | 18.11.2 | Test-only coverage extension |
| `xunit.v3.mtp-v2` | 4.0.1 | Test framework and Microsoft Testing Platform v2 runner |

The restored `tests/CodexUsageTray.Tests/obj/project.assets.json` also contains Microsoft Testing Platform 2.4.0, its MSBuild and telemetry extensions, Microsoft Application Insights 2.23.0, Microsoft Code Coverage dependencies, xUnit.net 4.0.1 runner components, and xUnit analyzers 2.1.0. Use `dotnet list tests/CodexUsageTray.Tests/CodexUsageTray.Tests.csproj package --include-transitive` to reproduce the exact graph.

The screenshot generator has no external package references. It only references the application project.

## Material embedded in the release

### .NET 10 Windows runtime and Windows Desktop Framework

The self-contained runtime packs are pinned to 10.0.12 through `RuntimeFrameworkVersion` and `TargetLatestRuntimePatch` in the application project. The repository also pins SDK 10.0.401 with roll-forward disabled. A dependency upgrade must update the notice inventory in the same change.

Microsoft states that self-contained and single-file applications embed .NET runtime parts. Its licensing guide also says that every .NET binary distribution must carry its license and third-party notice. See [.NET license information](https://github.com/dotnet/core/blob/main/license-information.md) and the [.NET asset licensing model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md).

On Windows, the [.NET Windows license map](https://github.com/dotnet/core/blob/main/license-information-windows.md) assigns the [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) to `coreclr.dll`, runtimes embedded in single-file binaries, and `Microsoft.DiaSymReader.Native.amd64.dll`. It assigns the MIT license to the other files in the current payload. A local inspection of a 10.0.12 publish found those restricted runtime parts, so the release notices must include the full .NET Library License, not only the MIT text.

The inspected WinForms publish also contained `WindowsBase.dll`, which comes from the WPF repository. It did not contain `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`, or `D3DCompiler_47_cor3.dll`. The Windows SDK License listed for `D3DCompiler_47_cor3.dll` therefore does not apply to the current artifact. Recheck this when the build settings or target framework changes.

For the current 10.0.12 payload, preserve these upstream texts:

| Component | Main license and copyright | Third-party notices |
| --- | --- | --- |
| .NET Runtime 10.0.12 | [MIT, `Copyright (c) .NET Foundation and Contributors`](https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/LICENSE.TXT) plus the [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) for the binaries listed above | [Complete Runtime notice file](https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/THIRD-PARTY-NOTICES.TXT) |
| Windows Forms 10.0.12 | [MIT, same .NET Foundation copyright](https://raw.githubusercontent.com/dotnet/winforms/v10.0.12/LICENSE.TXT) | [Complete WinForms notice file](https://raw.githubusercontent.com/dotnet/winforms/v10.0.12/THIRD-PARTY-NOTICES.TXT), including `Copyright © Sven Groot (Ookii.org) 2009` for Ookii.Dialogs |
| WPF-origin `WindowsBase.dll` 10.0.12 | [MIT, same .NET Foundation copyright](https://raw.githubusercontent.com/dotnet/wpf/v10.0.12/LICENSE.TXT) | [Complete WPF notice file](https://raw.githubusercontent.com/dotnet/wpf/v10.0.12/THIRD-PARTY-NOTICES.TXT), including zlib and Json.NET notices |

The three linked MIT files have the same license text and copyright line. A compiled notice may reproduce that MIT text once if its heading names all three .NET components. Preserve each upstream third-party notice file intact. Selective copying risks losing notices for code that the single-file bundler embedded.

## Test and development dependencies

These packages are present in the source and test dependency graph, but `build-release.ps1` does not publish the test project. They are not part of `CodexUsageTray.exe`. Keep their notices in the repository-wide notice inventory. They do not need to be represented as components of the released application unless the distribution also contains test binaries or test tooling.

### Microsoft Testing Platform 2.4.0

`xunit.v3.mtp-v2` brings in `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.Telemetry`, and `Microsoft.Testing.Extensions.TrxReport.Abstractions` 2.4.0. These packages come from [Microsoft TestFX](https://github.com/microsoft/testfx), declare MIT, and identify `Copyright (c) Microsoft Corporation`.

The telemetry extension brings in Microsoft Application Insights 2.23.0 and supporting Microsoft libraries. Their package metadata declares MIT. Preserve the common Microsoft MIT attribution already included in the notice file.

### Microsoft Code Coverage 18.11.2

The [Microsoft.Testing.Extensions.CodeCoverage 18.11.2 package](https://www.nuget.org/packages/Microsoft.Testing.Extensions.CodeCoverage/18.11.2) uses the Microsoft .NET Library license. Its package contains `License.txt` and `ThirdPartyNotices.txt`; preserve both files. The third-party notice identifies Mono.Cecil 0.11.3, Serilog 2.10.0, Serilog.Sinks.File 4.1.0, and Coverlet collector 3.1.2.

### xUnit.net 4.0.1 and analyzers 2.1.0

The [xunit.v3.mtp-v2 4.0.1 package](https://www.nuget.org/packages/xunit.v3.mtp-v2/4.0.1) restores the xUnit.net assertion library, core, extensibility, common runner, and in-process runner at 4.0.1. It also restores xUnit analyzers 2.1.0. The packages declare Apache-2.0 and identify `Copyright (C) .NET Foundation`.

- Include one complete copy of the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt).
- Preserve xUnit's license and the MIT notice for code adapted from MSBuild.
- Preserve the analyzer license and its MIT notice for code adapted from the Roslyn SDK.

## Continuous-integration dependencies

The build and release workflows reference `actions/checkout` v6, `actions/setup-dotnet` v5, `actions/upload-artifact` v6, and `actions/download-artifact` v7. Each uses the MIT license with `Copyright (c) 2018 GitHub, Inc. and contributors`. Preserve one copy of that common text. The tag-specific sources are the [checkout license](https://raw.githubusercontent.com/actions/checkout/v6/LICENSE), [setup-dotnet license](https://raw.githubusercontent.com/actions/setup-dotnet/v5/LICENSE), [upload-artifact license](https://raw.githubusercontent.com/actions/upload-artifact/v6/LICENSE), and [download-artifact license at the workflow's pinned commit](https://github.com/actions/download-artifact/blob/37930b1c2abaa49bbe596cd826c3c89aef350131/LICENSE).

The release workflow also uses `actions/attest` v4, whose [MIT license](https://github.com/actions/attest/blob/1e69f48acb82d1966a394da916b4c1698aa569d6/LICENSE) states `Copyright GitHub`. This attribution accompanies the shared MIT license in the notice inventory. The action runs in CI and is not bundled into the executable.

## External software that the app invokes

`CodexCommandLocator` searches for a separately installed `codex.cmd` or `codex.exe`, and `WindowsCodexProcessExecution` starts that file. The repository does not copy, package, download, or embed Codex CLI. OpenAI licenses the [Codex CLI repository](https://github.com/openai/codex/blob/main/docs/license.md) under Apache-2.0.

Record Codex CLI as an external prerequisite, not as redistributed software. Its license does not need to appear as a license for a component inside `CodexUsageTray.exe`. If a future installer starts bundling or downloading Codex CLI, audit the exact bundled CLI release and include its complete license and third-party notices.

## Items that are not third-party redistributions

- Calls to the GitHub Releases API and the `chatgpt.com` usage page are service integrations, not bundled libraries.
- Windows APIs, registry interfaces, shell-link COM interfaces, and the Segoe UI system font come from the user's operating system. The repository does not redistribute them.
- `Microsoft.NET.ILLink.Tasks` appears as an SDK build dependency in the application restore. The current release does not enable trimming and does not publish that package into the application.

## Notice-file assembly checklist

For the current binary release, include the .NET Library License, one labeled copy of the shared .NET MIT license, and the complete 10.0.12 Runtime, WinForms, and WPF third-party notice files. The project embeds these texts in the executable, exposes them through the tray menu, and copies both legal files beside the executable. The release workflow uploads the sidecar files as separate release assets.

For the repository-wide inventory, add the Microsoft Testing Platform, Microsoft Code Coverage, xUnit.net, and xUnit analyzer texts listed above. Include the full Apache License 2.0 once. Mark that section as test and development tooling.

Before each release, verify the resolved runtime-pack version and inspect the publish output for newly added restricted .NET or Windows SDK binaries. Regenerate the notice file if any direct package, transitive package, target framework, runtime identifier, or publish setting changes.
