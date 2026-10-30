param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$taskPluginRoot = $PSScriptRoot
$taskServerProject = Join-Path $taskPluginRoot 'server\ScreenGuide.Plugin.csproj'
$taskPublish = Join-Path $taskPluginRoot 'server\publish'
& dotnet publish $taskServerProject -c Release -o $taskPublish --nologo
if ($LASTEXITCODE -ne 0) { throw '插件适配器编译失败。' }
if ($SelfTest) {
    & (Join-Path $taskPublish 'ScreenGuide.Plugin.exe') --self-test
    if ($LASTEXITCODE -ne 0) { throw '插件协议自检失败。' }
}
Write-Host ('插件包已准备于：' + $taskPluginRoot)
Write-Host '此脚本只构建，不改变 Codex 安装状态；更新已安装插件后需刷新插件并重启 Codex。'
