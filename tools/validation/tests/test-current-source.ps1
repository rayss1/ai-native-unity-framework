param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [Parameter(Mandatory)][string]$SdkPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot '../run-current-source.ps1'
$arguments = @{SourceRoot=$SourceRoot;ExpectedCommit=$ExpectedCommit;SdkPath=$SdkPath;EvidenceDirectory=$EvidenceDirectory;Phases=@('Architecture')}
function Assert-Rejected([hashtable]$Options,[string]$Message) {
    $rejected = $false
    try { & $entry @Options } catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Message)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected rejection: $Message" }
}
$wrong = $arguments.Clone(); $wrong.ExpectedCommit = '0000000000000000000000000000000000000000'
$wrong.Profile = 'Candidate'; $wrong.ExpectedDotnetPassed = 333; $wrong.ExpectedEditModePassed = 147
Assert-Rejected $wrong 'Source identity mismatch'
$duplicate = $arguments.Clone(); $duplicate.Phases = @('Architecture','Architecture')
Assert-Rejected $duplicate 'Duplicate phases'
$empty = $arguments.Clone(); $empty.Phases = @()
Assert-Rejected $empty 'At least one validation phase'
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Test fixture directory must be new' }
$oldConnection = $env:AINATIVE_TEST_POSTGRES
try {
    $env:AINATIVE_TEST_POSTGRES = $null
    $missingDatabase = $arguments.Clone(); $missingDatabase.Phases = @('Dotnet')
    Assert-Rejected $missingDatabase 'Dotnet requires an isolated real PostgreSQL'
    $report = Get-Content (Join-Path $EvidenceDirectory 'validation.json') -Raw | ConvertFrom-Json
    if ($report.status -ne 'Failed' -or $report.phases.Count -ne 1 -or $report.phases[0].status -ne 'Failed') { throw 'Failed phase was reported as successful' }
    Assert-Rejected $arguments 'Use a new evidence directory'
} finally { $env:AINATIVE_TEST_POSTGRES = $oldConnection }
Write-Host 'Current-source entry: five rejection cases and persisted failed-phase status passed.'
