@echo off
setlocal EnableExtensions
cd /d "%~dp0"

where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is required to build a release.
    pause
    exit /b 1
)

dotnet --list-sdks | findstr /b "10." >nul
if errorlevel 1 (
    echo The .NET 10 SDK is required to build a release.
    pause
    exit /b 1
)

if exist "artifacts\win-x64" rmdir /s /q "artifacts\win-x64"

dotnet publish "src\CodexUsageTray\CodexUsageTray.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "artifacts\win-x64"
if errorlevel 1 (
    pause
    exit /b 1
)

"artifacts\win-x64\CodexUsageTray.exe" --self-test
if errorlevel 1 (
    echo Release self-test failed.
    pause
    exit /b 1
)

echo.
echo Release files ready in:
echo %~dp0artifacts\win-x64
echo.
echo Distribute the executable together with LICENSE.txt and THIRD-PARTY-NOTICES.txt.
pause
