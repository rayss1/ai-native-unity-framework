[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$RunDirectory,
 [Parameter(Mandatory)][string]$SdkPath,
 [Parameter(Mandatory)][string]$ExpectedSource,
 [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedFantasy,
 [Parameter(Mandatory)][string]$ReportPath,
 [ValidateRange(1,100000)][int]$MinimumCompletedCaptures=1,
 [ValidateRange(1,3600)][int]$VerifierTimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
$report=[IO.Path]::GetFullPath($ReportPath)
if(Test-Path -LiteralPath $report){throw 'Use a new replay report; retained evidence cannot be overwritten'}
$verifier=Join-Path $run 'bin/replay-verifier/AiNative.ArenaReplay.dll'
if(-not (Test-Path -LiteralPath $verifier)){throw 'Independently published verifier missing'}
$results=@();$incomplete=@();$passed=$false;$failure=$null
try {
 foreach($capture in @(Get-ChildItem -LiteralPath (Join-Path $run 'replay') -Recurse -File | Sort-Object FullName)) {
  if($capture.Extension -ne '.anar'){
   $incomplete+=@{path=$capture.FullName;bytes=$capture.Length;status='Unpublished'};continue
  }
  $stream=[IO.File]::OpenRead($capture.FullName)
  try {
   if($stream.Length -lt 26){throw 'Truncated published replay'}
   [void]$stream.Seek(-26,[IO.SeekOrigin]::End)
   $marker=$stream.ReadByte();[void]$stream.Seek(-1,[IO.SeekOrigin]::End);$status=$stream.ReadByte()
   if($marker -ne 255 -or $status -notin @(1,2,3,4)){throw 'Invalid published replay footer'}
  } finally {$stream.Dispose()}
  if($status -ne 1){$incomplete+=@{path=$capture.FullName;bytes=$capture.Length;status=$status};continue}
  $identityPath=Join-Path $run ($capture.Directory.Name+'.replay-identity.json')
  $identity=Get-Content -LiteralPath $identityPath -Raw|ConvertFrom-Json
  if($identity.Source -ne $ExpectedSource -or $identity.Fantasy -ne $ExpectedFantasy){throw 'Replay source/dependency identity differs from qualified build'}
  $psi=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($SdkPath))
  $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  $psi.Environment['DOTNET_ROOT']=Split-Path ([IO.Path]::GetFullPath($SdkPath))
  foreach($argument in @($verifier,$capture.FullName,$identity.Source,$identity.Fantasy,$identity.Protocol,$identity.Configuration)){$psi.ArgumentList.Add($argument)}
  $process=[Diagnostics.Process]::Start($psi)
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  try {
   if(-not $process.WaitForExit($VerifierTimeoutSeconds*1000)){throw 'Independent replay verifier timed out'}
   $output=$stdout.GetAwaiter().GetResult();$errorOutput=$stderr.GetAwaiter().GetResult()
   if($process.ExitCode -ne 0){throw ('Independent replay verifier rejected '+$capture.Name+': '+$errorOutput.Trim())}
   $result=$output|ConvertFrom-Json
   $results+=@{path=$capture.FullName;sha256=(Get-FileHash -LiteralPath $capture.FullName).Hash;identity=$identity;identitySha256=(Get-FileHash -LiteralPath $identityPath).Hash;result=$result}
  } finally {if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
 }
 if($results.Count -lt $MinimumCompletedCaptures){throw 'Completed replay inventory below required minimum'}
 $passed=$true
} catch { $failure=$_.Exception.Message;throw }
finally {
 @{passed=$passed;expectedSource=$ExpectedSource;expectedFantasy=$ExpectedFantasy;completedCount=$results.Count;
   verifierSha256=(Get-FileHash -LiteralPath $verifier).Hash;verified=$results;incomplete=$incomplete;failure=$failure;
   scope='Every published capture with a Complete footer is independently re-simulated; aborted/unpublished captures are retained separately';recordedUtc=[datetime]::UtcNow.ToString('O')}|
   ConvertTo-Json -Depth 15|Set-Content -LiteralPath $report
}
Write-Output ('Independently verified '+$results.Count+' complete replays; retained '+$incomplete.Count+' incomplete/aborted files.')
