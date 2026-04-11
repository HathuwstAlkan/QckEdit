@echo off
cd /d "%~dp0"

echo QckEdit - Build
echo -----------------

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo  ERROR: .NET 8 SDK not found.
    echo  Download from: https://dotnet.microsoft.com/download/dotnet/8.0
    echo  Install the SDK ^(not just the Runtime^), then run build.bat again.
    echo.
    pause
    exit /b 1
)

echo SDK found:
dotnet --version
echo.
echo Building... ^(~30 seconds^)
echo.

dotnet publish QckEdit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist

if errorlevel 1 (
    echo.
    echo  Build failed. See errors above.
    echo.
    pause
    exit /b 1
)

echo @echo off > dist\Uninstall.bat
echo cd /d "%%~dp0" >> dist\Uninstall.bat
echo if exist "QckEdit.exe" ( >> dist\Uninstall.bat
echo     start "" "QckEdit.exe" --uninstall >> dist\Uninstall.bat
echo ^) else ( >> dist\Uninstall.bat
echo     echo Error: QckEdit.exe not found. >> dist\Uninstall.bat
echo     pause >> dist\Uninstall.bat
echo ^) >> dist\Uninstall.bat

echo.
echo  ========================================
echo   SUCCESS
echo   dist\QckEdit.exe is ready.
echo   dist\Uninstall.bat is generated.
echo.
echo   Double-click QckEdit.exe to install.
echo  ========================================

echo.
echo Packaging into QckEdit.zip...
if exist "dist\QckEdit.zip" del "dist\QckEdit.zip"
powershell -Command "Compress-Archive -Path 'dist\QckEdit.exe', 'dist\Uninstall.bat' -DestinationPath 'dist\QckEdit.zip' -Force"
echo Done! Use dist\QckEdit.zip to share the app.

echo.
pause
