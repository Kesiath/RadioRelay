@echo off
setlocal
cd /d "%~dp0"

dotnet test RadioRelay.sln -c Release
if errorlevel 1 exit /b %errorlevel%

dotnet publish Client\RadioRelay.Client.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o artifacts\RadioRelay-win-x64

exit /b %errorlevel%
