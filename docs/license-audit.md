# Third-party license audit

Audit date: 2026-09-12

This report records the license sources needed to build the repository's third-party notice file. It separates software embedded in the released executable from tools used only to build or test the source. The upstream license and notice files linked below are the text to preserve. This report is not a substitute for those files.

## Repository evidence

The application project, `src/CodexUsageTray/CodexUsageTray.csproj`, has no `PackageReference` entries. `build-release.cmd` publishes it for `win-x64` with `--self-contained true` and `PublishSingleFile=true`. The executable therefore embeds .NET runtime and Windows Desktop Framework binaries.

The test project directly references these packages:

| Package | Version | Role |
| --- | ---: | --- |
| `coverlet.collector` | 6.0.4 | Test-only coverage collector |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | Test-only build and execution support |
| `xunit` | 2.9.3 | Test-only framework |
| `xunit.runner.visualstudio` | 3.1.4 | Test-only adapter |

The restored `tests/CodexUsageTray.Tests/obj/project.assets.json` also contains `Microsoft.CodeCoverage` 17.14.1, `Microsoft.TestPlatform.ObjectModel` 17.14.1, `Microsoft.TestPlatform.TestHost` 17.14.1, `Newtonsoft.Json` 13.0.3, `xunit.abstractions` 2.0.3, `xunit.analyzers` 1.18.0, `xunit.assert` 2.9.3, `xunit.core` 2.9.3, `xunit.extensibility.core` 2.9.3, and `xunit.extensibility.execution` 2.9.3.

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

These packages are present in the source and test dependency graph, but `build-release.cmd` does not publish the test project. They are not part of `CodexUsageTray.exe`. Keep their notices in the repository-wide notice inventory. They do not need to be represented as components of the released application unless the distribution also contains test binaries or test tooling.

### Coverlet 6.0.4

The [6.0.4 package](https://www.nuget.org/packages/coverlet.collector/6.0.4) declares MIT. Its package metadata identifies source commit `90b21079d43cffae3a18f264e00962b9c8a1d57a`.

- Preserve Coverlet's [MIT license](https://raw.githubusercontent.com/coverlet-coverage/coverlet/90b21079d43cffae3a18f264e00962b9c8a1d57a/LICENSE), including `Copyright (c) 2018 Toni Solarin-Sodara`.
- Preserve Coverlet's [complete third-party notice](https://raw.githubusercontent.com/coverlet-coverage/coverlet/90b21079d43cffae3a18f264e00962b9c8a1d57a/THIRD-PARTY-NOTICES.txt), which contains the ConsoleTables MIT notice and `Copyright (c) 2012 Khalid Abuhakmeh`.

### Microsoft test platform 17.14.1

`Microsoft.NET.Test.Sdk` depends on `Microsoft.TestPlatform.TestHost` and `Microsoft.CodeCoverage`. `TestHost` depends on `Microsoft.TestPlatform.ObjectModel` and `Newtonsoft.Json`. The [17.14.1 SDK package](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1) and all three Microsoft packages point to VSTest commit `490850ae3fdc1b470e3804ceab4f6a41cf89ae51` and declare MIT.

- Preserve the VSTest [MIT license](https://raw.githubusercontent.com/microsoft/vstest/490850ae3fdc1b470e3804ceab4f6a41cf89ae51/LICENSE), including `Copyright (c) Microsoft Corporation`.
- Preserve the [VSTest package notice](https://raw.githubusercontent.com/microsoft/vstest/490850ae3fdc1b470e3804ceab4f6a41cf89ae51/src/package/ThirdPartyNotices.txt). It includes notices for Newtonsoft.Json 13.0.3, Mono.Cecil 0.11.3, and NuGet.Client 6.8.0.117.
- Preserve the separate [Microsoft.CodeCoverage package notice](https://raw.githubusercontent.com/microsoft/vstest/490850ae3fdc1b470e3804ceab4f6a41cf89ae51/src/package/Microsoft.CodeCoverage/ThirdPartyNoticesCodeCoverage.txt). It contains the Mono.Cecil notice with `Copyright (c) 2008 - 2015 Jb Evain` and `Copyright (c) 2008 - 2011 Novell, Inc.`
- `Newtonsoft.Json` 13.0.3 also appears as a restored package. Preserve its [MIT license](https://raw.githubusercontent.com/JamesNK/Newtonsoft.Json/0a2e291c0d9c0c7675d445703e51750363a549ef/LICENSE.md), including `Copyright (c) 2007 James Newton-King`. The package's own metadata says `Copyright © James Newton-King 2008`; the license file is the controlling notice to reproduce.

The notice files inside the restored 17.14.1 packages match the linked VSTest source files. Their SHA-256 values in the audited restore were:

- `Microsoft.CodeCoverage/17.14.1/ThirdPartyNotices.txt`: `4FC4244935BBE3516164402C573F053A5D246571ED4D357E6CCCD8EBEC296BBC`
- `Microsoft.TestPlatform.TestHost/17.14.1/ThirdPartyNotices.txt`: `E2CE7628E0F67D0D724EEA9D9286A6938BF1EEDD253FC84B23D72AE54778A5F7`

### xUnit.net 2.9.3 and analyzers 1.18.0

The [xunit 2.9.3 package](https://www.nuget.org/packages/xunit/2.9.3) is a metapackage. It restores `xunit.assert`, `xunit.core`, `xunit.extensibility.core`, `xunit.extensibility.execution`, `xunit.abstractions`, and `xunit.analyzers`. The 2.9.3 packages point to xUnit commit `9712244020d385955d33136b3fe3e87de43539cd` and declare Apache-2.0. Their NuGet copyright field says `Copyright (C) .NET Foundation`.

- Include one complete copy of the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt). The xUnit attribution files quote the standard notice but do not reproduce all nine sections of the license.
- Preserve xUnit's [complete license file](https://raw.githubusercontent.com/xunit/xunit/9712244020d385955d33136b3fe3e87de43539cd/license.txt). Its main notice says `Copyright (c) .NET Foundation and Contributors` under Apache-2.0. The same file contains an MIT notice, `Copyright (c) 2015 .NET Foundation`, for imported .NET platform-abstractions and dependency-model code.
- `xunit.abstractions` 2.0.3 has no SPDX expression in its old package metadata. Its `licenseUrl` points to the same xUnit license file, so group it under the preceding complete text.
- Preserve the analyzer package's [complete license file](https://raw.githubusercontent.com/xunit/xunit.analyzers/2620a4ddabcba16ceb64ea9ad19c566252015fe8/LICENSE). It has the same .NET Foundation Apache-2.0 notice and a .NET Foundation MIT notice for code adapted from the Roslyn SDK.

### xUnit Visual Studio runner 3.1.4

The [runner package](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.4) declares Apache-2.0 and points to source commit `50e68bbb8b9ddd4b1bbb95d062b62010caf99909`. Its NuGet copyright field says `Copyright (C) .NET Foundation`.

Preserve the runner's [complete license file](https://raw.githubusercontent.com/xunit/visualstudio.xunit/50e68bbb8b9ddd4b1bbb95d062b62010caf99909/License.txt). It contains the main `Copyright (c) .NET Foundation and Contributors` Apache-2.0 notice and an MIT notice, `Copyright (c) 2015 .NET Foundation`, for imported platform-abstractions and dependency-model code.

## Continuous-integration dependencies

The build and release workflows reference `actions/checkout` v6, `actions/setup-dotnet` v5, and `actions/upload-artifact` v6. Each uses the MIT license with `Copyright (c) 2018 GitHub, Inc. and contributors`. Preserve one copy of that common text. The tag-specific sources are the [checkout license](https://raw.githubusercontent.com/actions/checkout/v6/LICENSE), [setup-dotnet license](https://raw.githubusercontent.com/actions/setup-dotnet/v5/LICENSE), and [upload-artifact license](https://raw.githubusercontent.com/actions/upload-artifact/v6/LICENSE).

## External software that the app invokes

`CodexCommandLocator` searches for a separately installed `codex.cmd` or `codex.exe`, and `WindowsCodexProcessExecution` starts that file. The repository does not copy, package, download, or embed Codex CLI. OpenAI licenses the [Codex CLI repository](https://github.com/openai/codex/blob/main/docs/license.md) under Apache-2.0.

Record Codex CLI as an external prerequisite, not as redistributed software. Its license does not need to appear as a license for a component inside `CodexUsageTray.exe`. If a future installer starts bundling or downloading Codex CLI, audit the exact bundled CLI release and include its complete license and third-party notices.

## Items that are not third-party redistributions

- Calls to the GitHub Releases API and the `chatgpt.com` usage page are service integrations, not bundled libraries.
- Windows APIs, registry interfaces, shell-link COM interfaces, and the Segoe UI system font come from the user's operating system. The repository does not redistribute them.
- `Microsoft.NET.ILLink.Tasks` appears as an SDK build dependency in the application restore. The current release does not enable trimming and does not publish that package into the application.

## Notice-file assembly checklist

For the current binary release, include the .NET Library License, one labeled copy of the shared .NET MIT license, and the complete 10.0.12 Runtime, WinForms, and WPF third-party notice files. The project embeds these texts in the executable, exposes them through the tray menu, and copies both legal files beside the executable. The release workflow uploads the sidecar files as separate release assets.

For the repository-wide inventory, add the full Coverlet, VSTest, Microsoft.CodeCoverage, Newtonsoft.Json, xUnit, xUnit analyzer, and xUnit Visual Studio runner texts listed above. Include the full Apache License 2.0 once. Mark that section as test and development tooling. Avoid replacing any full upstream notice with this report's summaries.

Before each release, verify the resolved runtime-pack version and inspect the publish output for newly added restricted .NET or Windows SDK binaries. Regenerate the notice file if any direct package, transitive package, target framework, runtime identifier, or publish setting changes.
