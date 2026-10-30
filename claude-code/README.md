# Claude Code 连接

先运行本目录的 Windows 本体（`app\ScreenGuide.exe`），退出其他版本的指路助手以避免共享连接冲突。另开 PowerShell，进入本目录后运行：

```powershell
.\claude-code\start.ps1
```

Linux X11 请先运行 `sh linux/start.sh`，再另开终端运行：

```sh
sh claude-code/start.sh
```

启动器为本次会话配置本地 MCP，不修改 Claude Code 全局设置或模型；按 Claude Code 自己的登录与模型选择使用。告诉 Claude 明确的软件和问题，让它依次读取、指出，自己点击或悬停；不会自动操作软件。可关闭 Claude 或本体停止会话。连续指路需要 Claude 当前回复保持等待，回复已经结束后可再说“继续”。混合模式将可见截图发到当前 Claude 会话，若不希望发图请切换“仅控件”。Windows 协议及 CLI 连接已检查，真实图形操作由用户验收；Linux 尚无真实桌面验收。

`-PrepareOnly`（Windows）或 `--prepare-only`（Linux）只生成本地连接配置，不启动 Claude。配置文件被视为本机文件，不应提交。
