@echo off
setlocal

rem Prefer the Windows Python launcher; forward all arguments unchanged.
where py >nul 2>nul
if not errorlevel 1 (
    py -3 "%~dp0manual.py" start %*
) else (
    where python >nul 2>nul
    if errorlevel 1 (
        echo Python 3.11+ is required. See README-local-debug.md.
        pause
        exit /b 1
    )
    python "%~dp0manual.py" start %*
)

set "manualExit=%ERRORLEVEL%"
if not "%manualExit%"=="0" pause
exit /b %manualExit%
