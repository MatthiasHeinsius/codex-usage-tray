param(
    [ValidateRange(1, 20)]
    [int]$Repetitions = 10,
    [ValidateSet('Targeted', 'Full')]
    [string]$Scope = 'Targeted',
    [ValidateSet('Both', 'On', 'Off')]
    [string]$Coverage = 'Both',
    [string]$Filter = '',
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../../TestResults/process-diagnostics')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Join-Path $PSScriptRoot '../../tests/CodexUsageTray.Tests/CodexUsageTray.Tests.csproj'
$method = 'CodexUsageTray.Tests.WindowsCodexProcessExecutionTests.ExchangeCancellationStopsBlockedIoAndTheChildProcess'
$summary = [System.Collections.Generic.List[object]]::new()

$coverageModes = switch ($Coverage) {
    'On' { @($true) }
    'Off' { @($false) }
    'Both' { @($true, $false) }
}
if ($Scope -eq 'Targeted' -and $Filter) {
    throw 'Use -Scope Full for a custom filter.'
}

foreach ($collectCoverage in $coverageModes) {
    $mode = if ($collectCoverage) { 'coverage' } else { 'without-coverage' }
    for ($iteration = 1; $iteration -le $Repetitions; $iteration++) {
        $runDirectory = Join-Path $ResultsDirectory "$mode-$iteration"
        # Separate directories prevent old results from being mistaken for this attempt.
        New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
        $runDirectory = (Resolve-Path -LiteralPath $runDirectory).Path
        $arguments = @(
            'test', '--project', $project, '-c', 'Release', '--no-build', '--no-restore',
            '--minimum-expected-tests', '4',
            '--timeout', '3m', '--results-directory', $runDirectory,
            '--report-xunit-trx', '--output', 'Detailed', '--no-ansi', '--no-progress'
        )
        if ($Scope -eq 'Targeted') {
            $arguments += @('--filter-method', $method)
        }
        elseif ($Filter) {
            $arguments += @('--filter', $Filter)
        }
        if ($collectCoverage) {
            $arguments += @('--coverage', '--coverage-output-format', 'cobertura')
        }

        Write-Host "Process cancellation diagnostics: $Scope, $mode, attempt $iteration/$Repetitions, filter=$Filter"
        & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'console.txt')
        $exitCode = $LASTEXITCODE
        $results = @(Get-ChildItem -LiteralPath $runDirectory -Filter '*.trx')
        $tests = @()
        if ($results.Count -eq 1) {
            [xml]$trx = Get-Content -LiteralPath $results[0].FullName -Raw
            $tests = @($trx.TestRun.Results.UnitTestResult)
        }
        $passed = @($tests | Where-Object { $_.outcome -eq 'Passed' }).Count
        $cancellationCases = @($tests | Where-Object { $_.testName.StartsWith($method + '(') })
        $valid = $exitCode -eq 0 -and $cancellationCases.Count -eq 4 -and $passed -eq $tests.Count
        if ($Scope -eq 'Targeted') { $valid = $valid -and $tests.Count -eq 4 }
        if ($Scope -eq 'Full' -and -not $Filter) { $valid = $valid -and $tests.Count -gt 4 }
        $summary.Add([pscustomobject]@{
            Scope = $Scope
            Filter = $Filter
            Mode = $mode
            Iteration = $iteration
            ExitCode = $exitCode
            Total = $tests.Count
            Passed = $passed
            CancellationCases = $cancellationCases.Count
            CancellationFailures = @($cancellationCases | Where-Object { $_.outcome -ne 'Passed' }).Count
            Succeeded = $valid
        })
        $summary | Export-Csv -LiteralPath (Join-Path $ResultsDirectory 'summary.csv') -NoTypeInformation
    }
}

$summary | Format-Table -AutoSize
if (@($summary | Where-Object { -not $_.Succeeded }).Count -gt 0) {
    throw 'Process diagnostics found failures. See the per-attempt TRX, console output, and summary.csv.'
}
