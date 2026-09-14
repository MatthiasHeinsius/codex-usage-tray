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
    $directNames = @{}
    $downloadDependencies = @{}
    foreach ($framework in $assets.project.frameworks.Values) {
        foreach ($name in $framework.dependencies.Keys) { $directNames[$name] = $true }
        foreach ($download in $framework.downloadDependencies) {
            if ($download.version -notmatch '^\[(\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?)(?:,\s*\1)?\]$') {
                throw "Download dependency $($download.name) does not have an exact version: $($download.version)"
            }
            $version = $Matches[1]
            $downloadDependencies["$($download.name)/$version"] = $download
        }
    }

    foreach ($targetName in $assets.targets.Keys) {
        $target = $assets.targets[$targetName]
        $resolved = @{}
        $packageIds = @{}
        $packageKeys = @($target.Keys | Where-Object { $target[$_].type -eq 'package' })
        foreach ($key in (@($packageKeys) + @($downloadDependencies.Keys) | Sort-Object -Unique)) {
            $name, $version = $key.Split('/', 2)
            $isDownload = $downloadDependencies.ContainsKey($key)
            $scope = if ($projectPath.StartsWith('src/') -and (!$isDownload -or $name -like '*.Runtime.*')) { 'runtime' } else { 'development' }
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
        version = '1.0.0'
        url = 'https://github.com/MatthiasHeinsius/codex-usage-tray'
    }
    manifests = $manifests
}
$snapshot | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Generated $($manifests.Count) resolved dependency manifests in $OutputPath."
