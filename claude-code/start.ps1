param(
    [switch]$PrepareOnly,
    [string]$ConfigPath,
    [string]$ClaudeCommand = 'claude',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ClaudeArguments = @()
)
$ErrorActionPreference = 'Stop'
$fieldReserved = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'reserved-options.json')) | ConvertFrom-Json
foreach ($fieldArgument in $ClaudeArguments) {
    foreach ($fieldOption in $fieldReserved) {
        if ($fieldArgument.Equals($fieldOption, [System.StringComparison]::OrdinalIgnoreCase) -or
            $fieldArgument.StartsWith($fieldOption + '=', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw ('指路助手 专用会话固定连接与流程提示，不接受覆盖参数：' + $fieldOption + '。请另行直接运行 claude 来使用其他连接或系统提示。')
        }
    }
}
$fieldRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fieldExecutable = Join-Path $fieldRoot 'codex-plugin\server\publish\ScreenGuide.Plugin.exe'
if (-not (Test-Path -LiteralPath $fieldExecutable -PathType Leaf)) {
    throw '找不到 指路助手 Windows 本体。请先发布 codex-plugin\server\publish\ScreenGuide.Plugin.exe，再运行此启动器。'
}
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $PSScriptRoot 'mcp.windows.local.json' }
$fieldConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
$fieldConfigParent = [System.IO.Path]::GetDirectoryName($fieldConfigPath)
[System.IO.Directory]::CreateDirectory($fieldConfigParent) | Out-Null
$fieldConfig = [ordered]@{
    mcpServers = [ordered]@{
        'screen-guide' = [ordered]@{ type = 'stdio'; command = $fieldExecutable; args = @() }
    }
}
$fieldTemporary = Join-Path $fieldConfigParent ('.screen-guide-mcp-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [System.IO.File]::WriteAllText($fieldTemporary, ($fieldConfig | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $fieldConfigPath) {
        [System.IO.File]::Replace($fieldTemporary, $fieldConfigPath, $null)
    } else {
        [System.IO.File]::Move($fieldTemporary, $fieldConfigPath)
    }
} finally {
    if (Test-Path -LiteralPath $fieldTemporary) { Remove-Item -LiteralPath $fieldTemporary }
}
if ($PrepareOnly) {
    [ordered]@{ prepared = $true; config = $fieldConfigPath; server = 'screen-guide'; globalSettingsChanged = $false } | ConvertTo-Json
    return
}
$fieldClaude = Get-Command -Name $ClaudeCommand -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $fieldClaude) { throw '找不到 Claude Code。请先安装并登录 Claude Code，或用 -ClaudeCommand 指定其入口。' }
$fieldInstructions = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GUIDE_PROMPT.md'), [System.Text.Encoding]::UTF8)
Write-Host '正在打开 指路助手 专用 Claude Code 会话。请先运行 指路助手 本体并在对话中明确目标软件。'
Write-Host '只为本会话载入 指路助手 MCP；不修改你的全局 MCP、账号或模型。'
& $fieldClaude.Source --mcp-config $fieldConfigPath --strict-mcp-config --append-system-prompt $fieldInstructions @ClaudeArguments
exit $LASTEXITCODE
