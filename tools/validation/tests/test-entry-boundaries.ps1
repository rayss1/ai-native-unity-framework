$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$entry=Join-Path $repo 'tools/validation/run-current-source.ps1'
$fixture=Join-Path $repo ('artifacts/validation-entry-stubs/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$SourceRoot=Join-Path $fixture 'source'; $TopologyRunDirectory='artifacts/run'
New-Item -ItemType Directory -Path (Join-Path $SourceRoot 'server/vendor/Fantasy') -Force | Out-Null
$ExpectedCommit='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $fantasy='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$SourceMode='CleanCommit'
$gitFailure=''
function git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE=0
    $submodule=$Arguments[1].Replace('\','/') -like '*server/vendor/Fantasy'
    if ($Arguments -contains 'diff' -and (($gitFailure -eq 'tracked' -and -not $submodule) -or ($gitFailure -eq 'nested-tracked' -and $submodule))) { $global:LASTEXITCODE=1; return }
    if ($Arguments -contains 'ls-files' -and (($gitFailure -eq 'unknown' -and -not $submodule) -or ($gitFailure -eq 'nested-unknown' -and $submodule))) { return 'untracked.cs' }
    if ($Arguments -contains 'rev-parse') {
        if ($Arguments -contains 'HEAD:server/vendor/Fantasy') { return $fantasy }
        if ($submodule) { if ($gitFailure -eq 'gitlink') { return 'cccccccccccccccccccccccccccccccccccccccc' }; return $fantasy }
        if ($gitFailure -eq 'commit') { return 'cccccccccccccccccccccccccccccccccccccccc' }; return $ExpectedCommit
    }
}
function Must-Reject([scriptblock]$Action,[string]$Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notmatch [regex]::Escape($Message)) { throw }; return }
    throw "Accepted invalid fixture: $Message"
}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Entry parsing failed'}
foreach($name in @('Assert-Source','Assert-TopologyPublication')) {
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    Invoke-Expression $function.Extent.Text
}
foreach($item in @(@('commit','Source identity mismatch'),@('tracked','Source has tracked modifications'),@('unknown','Source contains untracked files'),@('gitlink','Fantasy checkout differs'),@('nested-tracked','Fantasy checkout has modifications'),@('nested-unknown','Fantasy checkout contains untracked files'))) {
    $gitFailure=$item[0]; Must-Reject { Assert-Source } $item[1]
}
$gitFailure=''; [void](Assert-Source)
$run=Join-Path $SourceRoot $TopologyRunDirectory
New-Item -ItemType Directory -Path (Join-Path $SourceRoot 'infrastructure/topology') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $SourceRoot 'infrastructure/topology/Fantasy.config') 'fixture configuration'
$configHash=(Get-FileHash -LiteralPath (Join-Path $SourceRoot 'infrastructure/topology/Fantasy.config')).Hash
$publishedUtc=[datetime]::UtcNow.AddMinutes(-2);$processStart=[datetime]::UtcNow.AddMinutes(-1)
$ids=@('gate','player','lobby','match','coordinator','battle-1','battle-2')
foreach($id in $ids){
    $bin=Join-Path $run ('bin/'+$id);New-Item -ItemType Directory -Path $bin -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $bin 'host.exe') 'fixture executable'
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'infrastructure/topology/Fantasy.config') -Destination $bin
    @{Pid=123;Executable=(Join-Path $bin 'host.exe');StartUtc=$processStart.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run ($id+'.pid.json'))
}
Set-Content -LiteralPath (Join-Path $run 'bin/gate/one.dll') 'one'
Set-Content -LiteralPath (Join-Path $run 'bin/gate/two.dll') 'two'
$publication=@{source=$ExpectedCommit;fantasy=$fantasy;publishedUtc=$publishedUtc.ToString('O');fantasyConfigurationSha256=$configHash;files=@(Get-ChildItem (Join-Path $run 'bin') -File -Recurse|Where-Object Name -ne 'Fantasy.config'|ForEach-Object{@{path=[IO.Path]::GetRelativePath($run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})}
function Save-Publication {$publication|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'publication.json')}
function Get-Process {param($Id,$ErrorAction) return [pscustomobject]@{Path=$record.Executable;StartTime=$processStart}}
Save-Publication; Assert-TopologyPublication
$publication.source='wrong';Save-Publication;Must-Reject {Assert-TopologyPublication} 'publication source mismatch';$publication.source=$ExpectedCommit
$publication.fantasy='wrong';Save-Publication;Must-Reject {Assert-TopologyPublication} 'publication source mismatch';$publication.fantasy=$fantasy
$publication.files[0].sha256='wrong';Save-Publication;Must-Reject {Assert-TopologyPublication} 'binary identity mismatch'
$publication.files[0].sha256=(Get-FileHash -LiteralPath (Join-Path $run $publication.files[0].path)).Hash
$publication.fantasyConfigurationSha256='wrong';Save-Publication;Must-Reject {Assert-TopologyPublication} 'source configuration identity mismatch';$publication.fantasyConfigurationSha256=$configHash
Save-Publication;Remove-Item -LiteralPath (Join-Path $run 'publication.json');Must-Reject {Assert-TopologyPublication} 'TopologyPublish is required'
# No real SDK, PostgreSQL, build or testhost is used: the entry consumes these
# controlled native-command results and must persist their failure correctly.
$sdk=Join-Path $fixture 'sdk.ps1'
@'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$global:LASTEXITCODE=0
if($Arguments -contains '--version'){Write-Output '10.0.202';return}
if($Arguments[0] -eq 'build'){Add-Content -LiteralPath $env:AINATIVE_STUB_ORDER 'build';return}
if($env:AINATIVE_STUB_MODE -eq 'native-failure'){$global:LASTEXITCODE=1;return}
$directory=$Arguments[[Array]::IndexOf($Arguments,'--results-directory')+1]
if($Arguments -contains '--filter'){
 Add-Content -LiteralPath $env:AINATIVE_STUB_ORDER 'fresh'
 if($env:AINATIVE_STUB_MODE -eq 'missing'){return}
 $name=if($env:AINATIVE_STUB_MODE -eq 'wrong-test'){'WrongMethod'}else{'IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork'}
 $class='AiNative.Server.Fantasy.Tests.FantasyProbeInitializationTests'
 $count=1
}else{Add-Content -LiteralPath $env:AINATIVE_STUB_ORDER 'solution';$name='Example';$class='Fixture';$count=13}
for($index=0;$index -lt $count;$index++){
 "<TestRun><Results><UnitTestResult testId='one' outcome='Passed'/></Results><TestDefinitions><UnitTest id='one'><TestMethod className='$class' name='$name'/></UnitTest></TestDefinitions><ResultSummary outcome='Completed'><Counters total='1' executed='1' passed='1' failed='0' notExecuted='0'/></ResultSummary></TestRun>"|Set-Content -LiteralPath (Join-Path $directory "$index.trx")
}
'@ | Set-Content -LiteralPath $sdk
$oldPostgres=$env:AINATIVE_TEST_POSTGRES;$oldOrder=$env:AINATIVE_STUB_ORDER;$oldMode=$env:AINATIVE_STUB_MODE
Must-Reject { & $entry -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -SdkPath $sdk -EvidenceDirectory (Join-Path $fixture 'bad-baseline') -Phases Architecture } 'Baseline profile requires'
Must-Reject { & $entry -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -SdkPath $sdk -EvidenceDirectory (Join-Path $fixture 'implicit-candidate-count') -Profile Candidate -Phases Architecture } 'Candidate requires explicit'
try{
 $env:AINATIVE_TEST_POSTGRES='stub-no-network';$env:AINATIVE_STUB_ORDER=Join-Path $fixture 'order.txt'
 foreach($case in @(@('missing','exactly one fresh-process'),@('wrong-test','required test'),@('native-failure','exit 1'))){
  $env:AINATIVE_STUB_MODE=$case[0];$evidence=Join-Path $fixture $case[0]
  Must-Reject { & $entry -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -SdkPath $sdk -EvidenceDirectory $evidence -Profile Candidate -ExpectedDotnetPassed 13 -ExpectedEditModePassed 147 -Phases Dotnet } $case[1]
  $report=Get-Content -LiteralPath (Join-Path $evidence 'validation.json') -Raw|ConvertFrom-Json
  if($report.status -ne 'Failed' -or $report.phases[0].status -ne 'Failed'){throw 'Rejected independent gate was reported successful'}
 }
 $env:AINATIVE_STUB_MODE='valid';$evidence=Join-Path $fixture 'valid';Set-Content -LiteralPath $env:AINATIVE_STUB_ORDER ''
 & $entry -SourceRoot $SourceRoot -ExpectedCommit $ExpectedCommit -SdkPath $sdk -EvidenceDirectory $evidence -Profile Candidate -ExpectedDotnetPassed 13 -ExpectedEditModePassed 147 -Phases Dotnet
 $order=@(Get-Content -LiteralPath $env:AINATIVE_STUB_ORDER|Where-Object {$_})
 if(($order -join ',') -ne 'build,fresh,solution'){throw 'Fresh initialization was not isolated before the matrix'}
 $report=Get-Content -LiteralPath (Join-Path $evidence 'validation.json') -Raw|ConvertFrom-Json
 if($report.status -ne 'Passed' -or $report.phases[0].reports.Count -ne 13 -or $report.phases[0].freshProbe.Passed -ne 1){throw 'Entry report lost independent/matrix evidence'}
}finally{$env:AINATIVE_TEST_POSTGRES=$oldPostgres;$env:AINATIVE_STUB_ORDER=$oldOrder;$env:AINATIVE_STUB_MODE=$oldMode}
Write-Host 'Entry boundaries: six source failures, publication identity/hash/missing manifest, independent testhost ordering and persisted failure passed.'
