$ErrorActionPreference = 'Stop'
$demoRoot = $PSScriptRoot
$demoRuntime = Join-Path $demoRoot 'app-english'
$demoExe = Join-Path $demoRuntime 'ScreenGuide.exe'
if (-not (Test-Path -LiteralPath $demoExe)) {
    Push-Location $demoRoot
    try {
        dotnet publish .\ScreenGuide.csproj -c Release -o $demoRuntime --nologo
        if ($LASTEXITCODE -ne 0) { throw '编译失败，请检查 .NET 10 SDK。' }
    } finally { Pop-Location }
}
$localConfig = Join-Path $demoRoot 'config.local.json'
if (Test-Path -LiteralPath $localConfig) {
    Copy-Item -LiteralPath $localConfig -Destination (Join-Path $demoRuntime 'config.local.json')
}
Start-Process -FilePath $demoExe -WorkingDirectory $demoRoot
