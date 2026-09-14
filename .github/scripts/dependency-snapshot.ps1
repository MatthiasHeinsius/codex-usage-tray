#Requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [string]$CommitSha = $env:GITHUB_SHA,
    [string]$Ref = $env:GITHUB_REF
)

$ErrorActionPreference = 'Stop'
if ($CommitSha -notmatch '^[a-f0-9]{40}$' -or $Ref -notmatch '^refs/') {
    throw 'Provide the commit SHA and full Git ref that were restored.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$solution = Get-Content -LiteralPath (Join-Path $repositoryRoot 'CodexUsageTray.slnx') -Raw
$manifests = @{}
foreach ($project in $solution.Solution.Project) {
    $projectPath = $project.Path.Replace('\', '/')
    $projectDirectory = Split-Path (Join-Path $repositoryRoot $projectPath)
    $assetsPath = Join-Path $projectDirectory 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($targetName in $assets.targets.Keys) {
        $framework = $assets.project.frameworks[$targetName.Split('/')[0]]
        if (!$framework) { throw "No framework metadata for target $targetName in $projectPath." }
        $directNames = @{}
        $downloadDependencies = @{}
        foreach ($name in $framework.dependencies.Keys) { $directNames[$name] = $true }
        foreach ($download in $framework.downloadDependencies) {
            if ($download.version -notmatch '^\[(\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?)(?:,\s*\1)?\]$') {
                throw "Download dependency $($download.name) does not have an exact version: $($download.version)"
            }
            $version = $Matches[1]
            $downloadDependencies["$($download.name)/$version"] = $download
        }
        $target = $assets.targets[$targetName]
        $resolved = @{}
        $packageIds = @{}
        $packageKeys = @($target.Keys | Where-Object { $target[$_].type -eq 'package' })
        foreach ($key in (@($packageKeys) + @($downloadDependencies.Keys) | Sort-Object -Unique)) {
            $name, $version = $key.Split('/', 2)
            $isDownload = $downloadDependencies.ContainsKey($key)
            $hasRuntimeAssets = $false
            if ($isDownload) {
                # A restored pack can be unused, such as ASP.NET in this WinForms app.
                if ($name -match '^(.+)\.Runtime\.') {
                    $packFramework = $Matches[1]
                    $hasRuntimeAssets = @($framework.frameworkReferences.Keys | Where-Object {
                        $_ -eq $packFramework -or $_.StartsWith("$packFramework.")
                    }).Count -gt 0
                }
            }
            else {
                # Build/analyzer-only packages, including ILLink, are not shipped.
                $runtimeAssets = @($target[$key].runtime.Keys) + @($target[$key].native.Keys) + @($target[$key].runtimeTargets.Keys)
                $hasRuntimeAssets = @($runtimeAssets | Where-Object { $_ -and $_ -notmatch '(^|/)_\._$' }).Count -gt 0
            }
            $scope = if ($projectPath.StartsWith('src/') -and $hasRuntimeAssets) { 'runtime' } else { 'development' }
            $relationship = if ($directNames.ContainsKey($name) -or $isDownload) { 'direct' } else { 'indirect' }
            $resolved[$key] = @{
                package_url = 'pkg:nuget/{0}@{1}' -f [Uri]::EscapeDataString($name), [Uri]::EscapeDataString($version)
                relationship = $relationship
                scope = $scope
                dependencies = @()
            }
            $packageIds[$name] = $key
        }
        foreach ($key in $packageKeys) {
            $resolved[$key].dependencies = @(
                foreach ($name in $target[$key].dependencies.Keys) {
                    if ($packageIds.ContainsKey($name)) { $packageIds[$name] }
                }
            )
        }
        if ($resolved.Count -gt 0) {
            $manifestName = "${projectPath}::${targetName}"
            $manifests[$manifestName] = @{
                name = $manifestName
                file = @{ source_location = $projectPath }
                resolved = $resolved
            }
        }
    }
}
if ($manifests.Count -eq 0) { throw 'No resolved dependencies found. Restore the solution first.' }

$snapshot = @{
    version = 0
    sha = $CommitSha
    ref = $Ref
    scanned = [DateTime]::UtcNow.ToString('o')
    job = @{
        correlator = 'resolved-nuget'
        id = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local-submission' }
    }
    detector = @{
        name = 'codex-usage-tray-nuget-assets'
        version = '1.1.0'
        url = 'https://github.com/MatthiasHeinsius/codex-usage-tray'
    }
    manifests = $manifests
}
$snapshot | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Generated $($manifests.Count) resolved dependency manifests in $OutputPath."
