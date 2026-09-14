#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [string]$SnapshotPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/CodexUsageTray/CodexUsageTray.csproj') -Raw
$runtimeVersion = $project.Project.PropertyGroup.RuntimeFrameworkVersion
$snapshot = Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json -AsHashtable
$applicationManifests = 0
$testManifests = 0
foreach ($manifest in $snapshot.manifests.Values) {
    $resolved = $manifest.resolved
    foreach ($entry in $resolved.Values) {
        foreach ($dependency in $entry.dependencies) {
            if (!$resolved.ContainsKey($dependency)) { throw "Unresolved dependency edge: $dependency" }
        }
    }
    if ($manifest.file.source_location -eq 'src/CodexUsageTray/CodexUsageTray.csproj') {
        $applicationManifests++
        # The current application ships only these two runtime packs, not ASP.NET or ILLink.
        $expectedRuntime = @(
            "Microsoft.NETCore.App.Runtime.win-x64/$runtimeVersion"
            "Microsoft.WindowsDesktop.App.Runtime.win-x64/$runtimeVersion"
        )
        $actualRuntime = @($resolved.Keys | Where-Object { $resolved[$_].scope -eq 'runtime' })
        if (Compare-Object $expectedRuntime $actualRuntime) { throw "Unexpected runtime inventory in $($manifest.name)." }
        $ilLink = @($resolved.Keys | Where-Object { $_.StartsWith('Microsoft.NET.ILLink.Tasks/') })
        if ($ilLink.Count -ne 1 -or $resolved[$ilLink[0]].scope -ne 'development') {
            throw "Missing development dependency ILLink in $($manifest.name). Restore with PublishSingleFile=true."
        }
        foreach ($key in $resolved.Keys) {
            if ($key.StartsWith('Microsoft.AspNetCore.App.Runtime.') -and $resolved[$key].scope -ne 'development') {
                throw 'The unused ASP.NET runtime pack must remain a development dependency.'
            }
        }
    }
    else {
        if (@($resolved.Values | Where-Object { $_.scope -ne 'development' }).Count -gt 0) {
            throw "Non-application runtime dependencies in $($manifest.name)."
        }
        if ($manifest.file.source_location -eq 'tests/CodexUsageTray.Tests/CodexUsageTray.Tests.csproj') {
            $testManifests++
        }
    }
}
if ($applicationManifests -eq 0 -or $testManifests -eq 0) { throw 'Application or test dependencies are missing.' }
Write-Output 'Dependency snapshot regression checks passed.'
