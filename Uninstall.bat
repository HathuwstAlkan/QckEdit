@echo off
cd /d "%~dp0"

if exist "dist\QckEdit.exe" (
    "dist\QckEdit.exe" --uninstall
) else (
    echo Error: QckEdit.exe not found in dist folder.
    pause
    exit /b 1
)
pause
