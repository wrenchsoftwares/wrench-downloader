@echo off
setlocal enabledelayedexpansion

REM Navigate to repository root
cd /d "%~dp0"

echo ========================================================
echo         Wrench Downloader - Push to Main
echo ========================================================

REM Check if there are any changes
git status --porcelain > temp_git_status.txt
set /p STATUS=<temp_git_status.txt
del temp_git_status.txt

if "%STATUS%"=="" (
    echo No changes detected to commit.
    echo Checking if there are unpushed commits...
    git push origin main
    goto END
)

REM Prompt for commit message or use parameter
set "MSG=%~1"
if "%MSG%"=="" (
    set /p "MSG=Enter commit message (press Enter for auto message): "
)

if "%MSG%"=="" (
    for /f "tokens=1-4 delims=/ " %%a in ("%date%") do set "D=%%a-%%b-%%c"
    for /f "tokens=1-2 delims=:." %%a in ("%time%") do set "T=%%a:%%b"
    set "MSG=Update !D! !T!"
)

echo.
echo Staging changes...
git add -A

echo Committing: "!MSG!"
git commit -m "!MSG!"

echo.
echo Pushing to origin main...
git push origin main

:END
echo.
echo ========================================================
echo Done!
echo ========================================================
pause
