@echo off
setlocal
rem ============================================================================
rem  Builds APMultiTracker.exe into .\dist
rem  Needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
rem
rem    build.cmd          -> small EXE (~1 MB), needs the .NET 10 Desktop Runtime
rem    build.cmd full     -> standalone EXE, runs on any Windows 10/11 x64 PC
rem ============================================================================

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 10 SDK is not installed.
  echo Download it from https://dotnet.microsoft.com/download/dotnet/10.0
  start "" https://dotnet.microsoft.com/download/dotnet/10.0
  exit /b 1
)

set SC=false
set OUT=dist
if /i "%~1"=="full" (
  set SC=true
  set OUT=dist-standalone
)

dotnet publish "%~dp0src\APMultiTracker.csproj" -c Release -r win-x64 --self-contained %SC% ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ^
  -o "%~dp0%OUT%"
if errorlevel 1 exit /b 1

echo.
echo Done: %~dp0%OUT%\APMultiTracker.exe
endlocal
