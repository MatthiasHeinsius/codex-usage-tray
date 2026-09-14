#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

Push-Location -LiteralPath $PSScriptRoot
try {
    $globalJson = Join-Path $PSScriptRoot 'global.json'
    $requiredSdk = (Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json).sdk.version
    dotnet --version
    if ($LASTEXITCODE -ne 0) {
        throw "Install .NET SDK $requiredSdk, as pinned in global.json."
    }

    $outputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts\win-x64'))
    if (-not $outputDirectory.StartsWith($PSScriptRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The publish directory must be inside the repository.'
    }
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    dotnet publish .\src\CodexUsageTray\CodexUsageTray.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $outputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Publishing the release failed.'
    }

    $exe = Join-Path $outputDirectory 'CodexUsageTray.exe'
    # Start-Process interprets its working directory as a wildcard pattern.
    $process = Start-Process -FilePath $exe -ArgumentList '--self-test' `
        -WorkingDirectory ([WildcardPattern]::Escape($outputDirectory)) `
        -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Published executable self-test failed with exit code $($process.ExitCode)."
    }

    Write-Host "Release files ready in $outputDirectory"
    Write-Host 'Distribute the executable together with LICENSE.txt and THIRD-PARTY-NOTICES.txt.'
}
finally {
    Pop-Location
}
