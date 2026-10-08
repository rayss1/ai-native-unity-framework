$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. "$PSScriptRoot/../source-identity.ps1"
$entry=Join-Path $repo 'tools/validation/run-current-source.ps1'
$fixture=Join-Path $repo ('artifacts/worktree-entry-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$sdk=Join-Path $fixture 'sdk.ps1'
@'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$global:LASTEXITCODE=0
if($Arguments -contains '--version'){Write-Output '10.0.202';return}
if($Arguments[0] -eq 'build'){return}
$directory=$Arguments[[Array]::IndexOf($Arguments,'--results-directory')+1]
$count=if($Arguments -contains '--filter'){1}else{13}
$class=if($count -eq 1){'AiNative.Server.Fantasy.Tests.FantasyProbeInitializationTests'}else{'Fixture'}
$name=if($count -eq 1){'IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork'}else{'Example'}
for($index=0;$index -lt $count;$index++){
 "<TestRun><Results><UnitTestResult testId='one' outcome='Passed'/></Results><TestDefinitions><UnitTest id='one'><TestMethod className='$class' name='$name'/></UnitTest></TestDefinitions><ResultSummary outcome='Completed'><Counters total='1' executed='1' passed='1' failed='0' notExecuted='0'/></ResultSummary></TestRun>"|Set-Content -LiteralPath (Join-Path $directory "$index.trx")
}
'@ | Set-Content -LiteralPath $sdk
$options=@{SourceRoot=$repo;ExpectedCommit=(& git -C $repo rev-parse HEAD).Trim();SdkPath=$sdk;Profile='TerminalDelivery';ExpectedDotnetPassed=13;ExpectedEditModePassed=164;Phases=@('Dotnet')}
function Must-Reject([hashtable]$Arguments,[string]$Message){
    try{& $entry @Arguments | Out-Null}catch{if($_.Exception.Message -notmatch [regex]::Escape($Message)){throw};return}
    throw "Accepted invalid worktree invocation: $Message"
}
$invalid=$options.Clone();$invalid.EvidenceDirectory=Join-Path $fixture 'missing-hash';$invalid.SourceMode='Worktree'
Must-Reject $invalid 'explicit ExpectedSourceManifestSha256'
$invalid=$options.Clone();$invalid.EvidenceDirectory=Join-Path $fixture 'wrong-hash';$invalid.SourceMode='Worktree';$invalid.ExpectedSourceManifestSha256='0'*64
Must-Reject $invalid 'manifest mismatch'
$invalid=$options.Clone();$invalid.EvidenceDirectory=Join-Path $fixture 'clean-with-hash';$invalid.ExpectedSourceManifestSha256='0'*64
Must-Reject $invalid 'only valid with Worktree'
$invalid=$options.Clone();$invalid.EvidenceDirectory=Join-Path $fixture 'worktree-publish';$invalid.SourceMode='Worktree';$invalid.ExpectedSourceManifestSha256='0'*64;$invalid.Phases=@('TopologyPublish')
Must-Reject $invalid 'Topology qualification requires CleanCommit'
$manifest=Get-AiNativeSourceManifest -Root $repo
$options.SourceMode='Worktree';$options.ExpectedSourceManifestSha256=$manifest.sha256;$options.EvidenceDirectory=Join-Path $fixture 'valid'
$oldPostgres=$env:AINATIVE_TEST_POSTGRES
try{
    $env:AINATIVE_TEST_POSTGRES='stub-no-network'
    & $entry @options
    $report=Get-Content -LiteralPath (Join-Path $options.EvidenceDirectory 'validation.json') -Raw | ConvertFrom-Json
    if($report.status -ne 'Passed' -or $report.sourceMode -ne 'Worktree' -or $report.releaseQualified -ne $false -or $report.sourceManifest.sha256 -ne $manifest.sha256 -or $report.phases[0].passed -ne 13){throw 'Worktree report lost its development identity/results'}
}finally{$env:AINATIVE_TEST_POSTGRES=$oldPostgres}
Write-Host 'Worktree entry: explicit hash/mode, topology rejection and retained development identity passed (stub SDK, no database/Unity).'
