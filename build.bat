@echo off
cd /d "%~dp0"

echo QckEditor - Build
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

dotnet publish QckEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist

if errorlevel 1 (
    echo.
    echo  Build failed. See errors above.
    echo.
    pause
    exit /b 1
)

echo.
echo  ========================================
echo   SUCCESS
echo   dist\QckEditor.exe is ready.
echo.
echo   Double-click it to install.
echo   Distribute just that one file.
echo  ========================================
echo.
pause
