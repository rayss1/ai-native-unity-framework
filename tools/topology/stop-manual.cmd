@echo off
setlocal

rem Use the first available Python installation and preserve its exit code.
where py >nul 2>nul
if not errorlevel 1 (
    py -3 -X utf8 "%~dp0manual.py" stop %*
    goto complete
)

where python >nul 2>nul
if not errorlevel 1 (
    python -X utf8 "%~dp0manual.py" stop %*
    goto complete
)

where python3 >nul 2>nul
if not errorlevel 1 (
    python3 -X utf8 "%~dp0manual.py" stop %*
    goto complete
)

rem Support common per-user Conda installs when their directory is not on PATH.
if exist "%USERPROFILE%\anaconda3\python.exe" (
    "%USERPROFILE%\anaconda3\python.exe" -X utf8 "%~dp0manual.py" stop %*
    goto complete
)

if exist "%USERPROFILE%\miniconda3\python.exe" (
    "%USERPROFILE%\miniconda3\python.exe" -X utf8 "%~dp0manual.py" stop %*
    goto complete
)

echo Python 3.11+ is required. See README-local-debug.md.
pause
exit /b 1

:complete
set "manualExit=%ERRORLEVEL%"
if not "%manualExit%"=="0" pause
exit /b %manualExit%
