@echo off
setlocal
set "manualPwsh="
for /f "delims=" %%P in ('%SystemRoot%\System32\where.exe pwsh.exe 2^>nul') do if not defined manualPwsh set "manualPwsh=%%P"
if not defined manualPwsh if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "manualPwsh=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined manualPwsh if exist "%ProgramW6432%\PowerShell\7\pwsh.exe" set "manualPwsh=%ProgramW6432%\PowerShell\7\pwsh.exe"
if not defined manualPwsh if exist "%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe" set "manualPwsh=%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe"
if not defined manualPwsh if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\pwsh.exe" set "manualPwsh=%LOCALAPPDATA%\Microsoft\WinGet\Links\pwsh.exe"
if not defined manualPwsh if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "manualPwsh=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined manualPwsh (
    echo PowerShell 7 was not found in PATH or known installation directories.
    echo Install PowerShell 7, then reopen this launcher.
    pause
    exit /b 1
)
pushd "%~dp0..\.."
"%manualPwsh%" -NoProfile -File "%~dp0start-manual.ps1" %*
set "manualExit=%ERRORLEVEL%"
popd
if not "%manualExit%"=="0" pause
exit /b %manualExit%
