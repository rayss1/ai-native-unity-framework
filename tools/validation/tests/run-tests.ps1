#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Portable', 'Windows')][string]$Suite = 'All'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$portableTests = @(
    'test-nunit-report.ps1',
    'test-validation-contracts.ps1',
    'test-entry-boundaries.ps1',
    'test-source-profiles.ps1',
    'test-worktree-entry.ps1',
    'test-unity-generated-state.ps1',
    'test-unity-test-contract.ps1'
)
$windowsTests = @('test-windows-process-cleanup.ps1')
# This parameterized integration test needs a caller-provided checkout and SDK.
# Keep it visible in the inventory without silently treating it as a portable pass.
$integrationTests = @('test-current-source.ps1')
$knownTests = $portableTests + $windowsTests + $integrationTests
$actualTests = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'test-*.ps1' -File |
        Select-Object -ExpandProperty Name)
if (Compare-Object $knownTests $actualTests) {
    throw 'Validation test inventory changed. Assign each test to a suite in run-tests.ps1.'
}
if ($Suite -eq 'Windows' -and -not $IsWindows) {
    throw 'The Windows validation suite requires a Windows host.'
}

$selectedTests = @()
if ($Suite -ne 'Windows') {
    $selectedTests += $portableTests
}
if ($Suite -ne 'Portable' -and $IsWindows) {
    $selectedTests += $windowsTests
} elseif ($Suite -eq 'All') {
    Write-Host 'Skipped Windows suite: this host is not Windows.'
}

# Each test owns its process: mocked commands, environment and exit state cannot
# leak into subsequent tests. A failed child always fails the suite.
$pwsh = (Get-Process -Id $PID).Path
foreach ($test in $selectedTests) {
    Write-Host "Running $test"
    & $pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot $test)
    if ($LASTEXITCODE -ne 0) {
        throw "$test failed with exit code $LASTEXITCODE"
    }
}
Write-Host "Validation $Suite suite: $($selectedTests.Count) scripts passed."
Write-Host 'Not run: test-current-source.ps1 (explicit checkout/SDK integration test).'
