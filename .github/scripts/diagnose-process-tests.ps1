param(
    [ValidateRange(1, 20)]
    [int]$Repetitions = 10,
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../../TestResults/process-diagnostics')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Join-Path $PSScriptRoot '../../tests/CodexUsageTray.Tests/CodexUsageTray.Tests.csproj'
$method = 'CodexUsageTray.Tests.WindowsCodexProcessExecutionTests.ExchangeCancellationStopsBlockedIoAndTheChildProcess'
$summary = [System.Collections.Generic.List[object]]::new()

foreach ($coverage in @($true, $false)) {
    $mode = if ($coverage) { 'coverage' } else { 'without-coverage' }
    for ($iteration = 1; $iteration -le $Repetitions; $iteration++) {
        $runDirectory = Join-Path $ResultsDirectory "$mode-$iteration"
        # Separate directories prevent old results from being mistaken for this attempt.
        New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
        $runDirectory = (Resolve-Path -LiteralPath $runDirectory).Path
        $arguments = @(
            'test', '--project', $project, '-c', 'Release', '--no-build', '--no-restore',
            '--filter-method', $method, '--minimum-expected-tests', '4',
            '--timeout', '2m', '--results-directory', $runDirectory,
            '--report-xunit-trx', '--output', 'Detailed', '--no-ansi', '--no-progress'
        )
        if ($coverage) {
            $arguments += @('--coverage', '--coverage-output-format', 'cobertura')
        }

        Write-Host "Process cancellation diagnostics: $mode, attempt $iteration/$Repetitions"
        & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'console.txt')
        $exitCode = $LASTEXITCODE
        $results = @(Get-ChildItem -LiteralPath $runDirectory -Filter '*.trx')
        $tests = @()
        if ($results.Count -eq 1) {
            [xml]$trx = Get-Content -LiteralPath $results[0].FullName -Raw
            $tests = @($trx.TestRun.Results.UnitTestResult)
        }
        $passed = @($tests | Where-Object { $_.outcome -eq 'Passed' }).Count
        $valid = $exitCode -eq 0 -and $tests.Count -eq 4 -and $passed -eq 4
        $summary.Add([pscustomobject]@{
            Mode = $mode
            Iteration = $iteration
            ExitCode = $exitCode
            Total = $tests.Count
            Passed = $passed
            Succeeded = $valid
        })
        $summary | Export-Csv -LiteralPath (Join-Path $ResultsDirectory 'summary.csv') -NoTypeInformation
    }
}

$summary | Format-Table -AutoSize
if (@($summary | Where-Object { -not $_.Succeeded }).Count -gt 0) {
    throw 'Process diagnostics found failures. See the per-attempt TRX, console output, and summary.csv.'
}
