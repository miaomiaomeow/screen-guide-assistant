# Linux X11 试验版

保留 Windows 精简版，Linux 是独立实现，尚未在真实 Linux 桌面验收。仅支持 X11、100% 缩放、有透明合成的桌面；不支持 Wayland 或 macOS。需要 Python 3、GTK 3/PyGObject、python-xlib、Pillow、AT-SPI 及支持 EWMH 的窗口管理器。以 Debian/Ubuntu 为例，安装发行版提供的 `python3-gi gir1.2-gtk-3.0 python3-xlib python3-pil gir1.2-atspi-2.0` 后运行：

```sh
sh linux/start.sh
```

通过 Codex 或 Claude Code 的 MCP 连接让当前会话模型判断；本体不配置或调用模型。先启动本体，再在 Agent 中明确要操作的软件和问题。默认混合模式读取控件及截图；可在小窗切换，并保存所选模式。截图会发送给当前 Agent，请按自己的隐私要求选择模式。黄框为核实过的控件，白框为图片估计。用户自行操作；清除按钮或 Esc 结束当前指路。

GTK 和 Linux AT-SPI 在不同应用的控件暴露程度不同。此试验版没有本体语音识别或本地模型，点击侦测通过短周期轮询，极快的点击或按键可能漏报；没有真实 Linux 图形环境验证，不能保证跨发行版或复杂子菜单可用。目标移动、消失或切换时清除高亮。设置只保存屏幕模式，不保存对话或截图。
