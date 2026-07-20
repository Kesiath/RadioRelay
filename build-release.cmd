@echo off
setlocal

cd /d "%~dp0"

set "PUBLISH_ROOT=%CD%\publish"
set "CLIENT_OUTPUT=%PUBLISH_ROOT%\Client"
set "SERVER_OUTPUT=%PUBLISH_ROOT%\Server"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: The .NET SDK was not found in PATH.
    exit /b 1
)

echo Cleaning release folders...
if exist "%CLIENT_OUTPUT%" rmdir /s /q "%CLIENT_OUTPUT%"
if exist "%SERVER_OUTPUT%" rmdir /s /q "%SERVER_OUTPUT%"

echo Publishing RadioRelay client for Windows x64...
dotnet publish "Client\RadioRelay.Client.csproj" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%CLIENT_OUTPUT%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
if errorlevel 1 goto :failed

echo Publishing RadioRelay server for Windows x64...
dotnet publish "Server\RadioRelay.Server.csproj" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%SERVER_OUTPUT%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
if errorlevel 1 goto :failed

if not exist "%CLIENT_OUTPUT%\RadioRelay.exe" (
    echo ERROR: Client executable was not created.
    goto :failed
)

if not exist "%SERVER_OUTPUT%\RadioRelay.Server.exe" (
    echo ERROR: Server executable was not created.
    goto :failed
)

echo.
echo Release build complete:
echo   Client: "%CLIENT_OUTPUT%\RadioRelay.exe"
echo   Server: "%SERVER_OUTPUT%\RadioRelay.Server.exe"
exit /b 0

:failed
echo.
echo ERROR: Release build failed.
exit /b 1
