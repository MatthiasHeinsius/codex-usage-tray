#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$projectPath = Join-Path $repositoryRoot 'src/CodexUsageTray/CodexUsageTray.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$pinnedRuntime = [version]$project.Project.PropertyGroup.RuntimeFrameworkVersion
$channel = '{0}.{1}' -f $pinnedRuntime.Major, $pinnedRuntime.Minor
$metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
$metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 30

if ($metadata.'support-phase' -eq 'eol') {
    throw ".NET $channel is out of support. Upgrade the target framework, SDK, runtime, and license inventory."
}

$latestRuntime = [version]$metadata.'latest-runtime'
if ($pinnedRuntime -ne $latestRuntime) {
    throw "Bundled runtime $pinnedRuntime differs from the current .NET $channel runtime $latestRuntime. Update RuntimeFrameworkVersion, review THIRD-PARTY-NOTICES.txt and docs/license-audit.md, and rebuild the release."
}

Write-Output "Bundled .NET runtime $pinnedRuntime is current for the supported $channel channel."
