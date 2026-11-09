$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:USERPROFILE\.dotnet;$env:LOCALAPPDATA\Android\Sdk\platform-tools;$env:PATH"

$dotnetExe = "$env:USERPROFILE\.dotnet\dotnet.exe"
if (Test-Path $dotnetExe) {
    & $dotnetExe run --project "$PSScriptRoot\windows-host\src\SecondScreen.App\SecondScreen.App.csproj" @args
} else {
    dotnet run --project "$PSScriptRoot\windows-host\src\SecondScreen.App\SecondScreen.App.csproj" @args
}

