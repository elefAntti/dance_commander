@echo off
setlocal

taskkill /IM DanceCommander.exe /F >nul 2>&1

dotnet publish "%~dp0..\src\DanceCommander.App\DanceCommander.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o "%LOCALAPPDATA%\DanceCommander" ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true

endlocal
