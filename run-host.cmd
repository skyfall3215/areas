@echo off
set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
set "PATH=%USERPROFILE%\.dotnet;%LOCALAPPDATA%\Android\Sdk\platform-tools;%PATH%"
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    "%USERPROFILE%\.dotnet\dotnet.exe" run --project "%~dp0windows-host\src\SecondScreen.App\SecondScreen.App.csproj" %*
) else (
    dotnet run --project "%~dp0windows-host\src\SecondScreen.App\SecondScreen.App.csproj" %*
)

