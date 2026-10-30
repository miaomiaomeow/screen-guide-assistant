#!/usr/bin/env python3
"""X11 desktop entry and MCP bridge; no automatic software operation."""
import os, sys

def main():
    if '--mcp' in sys.argv:
        from guide_mcp import run_mcp
        return run_mcp()
    if not sys.platform.startswith('linux'):
        print('Linux 版本只能在 Linux X11 桌面运行。', file=sys.stderr); return 2
    if not os.environ.get('DISPLAY') or os.environ.get('XDG_SESSION_TYPE', '').lower() == 'wayland':
        print('暂只支持 X11 桌面；Wayland 暂不支持。', file=sys.stderr); return 2
    try:
        import gi
        gi.require_version('Gtk', '3.0')
        from gi.repository import Gtk
        import Xlib, PIL
        from guide_ui import run
        return run()
    except (ImportError, ValueError) as e:
        print('缺少 GTK3、python3-gi、python3-xlib 或 python3-pil：' + str(e), file=sys.stderr); return 2
    except Exception as e:
        print('无法启动 Linux 指路助手：' + str(e), file=sys.stderr); return 2

if __name__ == '__main__': sys.exit(main())
