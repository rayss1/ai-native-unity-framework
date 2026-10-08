$ErrorActionPreference='Stop'
. "$PSScriptRoot/../unity-generated-state.ps1"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../artifacts/generated-state-tests'))
$project = Join-Path $root ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $project 'ProjectSettings') -Force | Out-Null
$csproj = Join-Path $project 'Existing.csproj'
$shader = Join-Path $project 'ProjectSettings/ShaderGraphSettings.asset'
[IO.File]::WriteAllText($csproj,'original generated project')
[IO.File]::WriteAllText($shader,"setting: 1`r`n")
$graphics = Join-Path $project 'ProjectSettings/GraphicsSettings.asset'
[IO.File]::WriteAllText($graphics,"graphics: 1`r`n")
$saved = Save-AiNativeUnityGeneratedState -ProjectRoot $project
[IO.File]::WriteAllText($csproj,'changed machine paths')
[IO.File]::WriteAllText($shader,"setting: 1`n")
[IO.File]::WriteAllText($graphics,"graphics: 1`n")
$newProject = Join-Path $project 'Fantasy.Unity.csproj'
[IO.File]::WriteAllText($newProject,'<!-- Generated file, do not modify -->')
$unknown = Join-Path $project 'Unknown.csproj'
[IO.File]::WriteAllText($unknown,'user file')
[void](Restore-AiNativeUnityGeneratedState -Snapshot $saved)
if ([IO.File]::ReadAllText($csproj) -ne 'original generated project' -or (Test-Path $newProject) -or -not (Test-Path $unknown) -or [IO.File]::ReadAllText($shader) -cne "setting: 1`r`n" -or [IO.File]::ReadAllText($graphics) -cne "graphics: 1`r`n") { throw 'Generated-state restoration failed' }
[IO.File]::WriteAllText($shader,"setting: 2`n")
$rejected=$false
try { [void](Restore-AiNativeUnityGeneratedState -Snapshot $saved) } catch { if ($_.Exception.Message -notmatch 'beyond line endings') { throw }; $rejected=$true }
if (-not $rejected -or [IO.File]::ReadAllText($shader) -ne "setting: 2`n") { throw 'Substantive asset change was hidden' }
[IO.File]::WriteAllText($newProject,'user authored content')
$rejected=$false
try { [void](Restore-AiNativeUnityGeneratedState -Snapshot $saved) } catch { if ($_.Exception.Message -notmatch 'unknown project') { throw }; $rejected=$true }
if (-not $rejected -or -not (Test-Path $newProject)) { throw 'Unknown project was removed' }
Write-Host 'Unity generated files restored; substantive settings and unknown files preserved/rejected.'
