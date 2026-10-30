$ErrorActionPreference = 'Stop'
$taskPluginExe = Join-Path $PSScriptRoot 'server\publish\ScreenGuide.Plugin.exe'
if (-not (Test-Path -LiteralPath $taskPluginExe)) { throw '请先运行 .\build.ps1，或直接运行 .\build.ps1 -SelfTest。' }
& $taskPluginExe --self-test
if ($LASTEXITCODE -ne 0) { throw '插件协议自检失败。' }
