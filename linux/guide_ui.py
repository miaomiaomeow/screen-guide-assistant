"""Compact GTK host. All X11 and GTK access is confined to the GTK thread."""
import concurrent.futures, json, os, threading, time
from pathlib import Path
from gi.repository import Gtk, Gdk, GLib
from guide_core import GuideRuntime, GuideError, MODES
from guide_native import X11Backend
from guide_mcp import LocalServer

CSS = b'''window {background: #111; color: #fafafa;} box {color: #fafafa;} button, combobox, entry {background: #1e1e1e; color: #fafafa; border: 1px solid #444; border-radius: 8px; padding: 5px;} button:hover {border-color: #ffd43b;} label {color: #fafafa;}'''

class Overlay(Gtk.Window):
    def __init__(self):
        super().__init__(type=Gtk.WindowType.TOPLEVEL)
        self.set_decorated(False); self.set_app_paintable(True); self.set_accept_focus(False)
        self.set_focus_on_map(False); self.set_keep_above(True); self.set_skip_taskbar_hint(True)
        visual = self.get_screen().get_rgba_visual()
        if not visual or not self.get_screen().is_composited(): raise GuideError('高亮需要 X11 桌面启用透明合成。')
        self.set_visual(visual); self.frame = None
        self.connect('draw', self.draw)
    def show_frame(self, frame):
        self.frame = frame; b = frame['bounds']; pad = 5
        self.move(int(b.x)-pad, int(b.y)-pad); self.resize(int(b.width)+pad*2, int(b.height)+pad*2)
        self.show_all(); self.get_window().set_pass_through(True); self.queue_draw()
    def clear(self): self.frame = None; self.hide()
    def draw(self, widget, cr):
        cr.set_operator(0); cr.paint(); cr.set_operator(2)
        cr.set_source_rgba(*( (1,.83,.23,1) if self.frame and self.frame['control'] else (1,1,1,1) ))
        cr.set_line_width(3); cr.rectangle(3,3,self.get_allocated_width()-6,self.get_allocated_height()-6); cr.stroke()
        return False

class Host:
    def __init__(self):
        if Gdk.Screen.get_default().get_monitor_scale_factor(0) != 1: raise GuideError('Linux 首版仅支持显示缩放为 100%。')
        self.backend = X11Backend(); self.overlay = Overlay()
        self.window = Gtk.Window(title='屏幕指路助手'); self.window.set_keep_above(True); self.window.set_default_size(390, -1)
        self.window.connect('destroy', self.quit)
        self.backend.own_windows = lambda: [self.window, self.overlay]
        self.backend.flush_gui = lambda: Gdk.Display.get_default().sync()
        self.backend.overlay_clear = self.overlay.clear; self.backend.show_frame = self.overlay.show_frame
        self.backend.own_foreground = lambda: bool(self.window.is_active())
        self.runtime = GuideRuntime(self.backend, self.message)
        self.settings = Path(os.environ.get('XDG_CONFIG_HOME', str(Path.home()/'.config'))) / 'screen-guide' / 'settings.json'
        try:
            saved = json.loads(self.settings.read_text(encoding='utf8'))
            if saved.get('mode') in MODES: self.runtime.preferred_mode = saved['mode']
        except (OSError, ValueError, AttributeError): pass
        provider = Gtk.CssProvider(); provider.load_from_data(CSS)
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=7); box.set_border_width(10); self.window.add(box)
        header = Gtk.Box(spacing=7); box.pack_start(header, False, False, 0)
        fold = Gtk.Button(label='指路助手  ▾'); header.pack_start(fold, True, True, 0)
        clear = Gtk.Button(label='清除'); clear.connect('clicked', lambda *_: self.runtime.pause('已清除。', True)); header.pack_start(clear, False, False, 0)
        exit_button = Gtk.Button(label='退出'); exit_button.connect('clicked', lambda *_: self.window.destroy()); header.pack_start(exit_button, False, False, 0)
        self.details = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6); box.pack_start(self.details, False, False, 0)
        fold.connect('clicked', lambda *_: self.details.hide() if self.details.get_visible() else self.details.show_all())
        self.mode = Gtk.ComboBoxText()
        for mode, label in [('hybrid','混合：控件＋截图'),('controls','仅控件'),('direct_image','直接截图'),('image_on_failure','控件失败时截图')]: self.mode.append(mode,label)
        self.mode.set_active_id(self.runtime.preferred_mode); self.mode.connect('changed', self.save_mode); self.details.pack_start(self.mode, False,False,0)
        self.status = Gtk.Label(label='请在 Codex 或 Claude Code 中提出问题。'); self.status.set_line_wrap(True); self.status.set_xalign(0); self.details.pack_start(self.status, False,False,0)
        self.last_buttons = 0; self.last_escape = False
        self.server = LocalServer(self.dispatch)
        self.window.show_all(); GLib.timeout_add(90, self.tick)
    def message(self, value):
        if hasattr(self, 'status'): self.status.set_text(str(value)[:240])
    def save_mode(self, widget):
        mode = widget.get_active_id()
        if mode not in MODES: return
        self.runtime.preferred_mode = mode
        try:
            self.settings.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            temporary = self.settings.with_suffix('.tmp')
            fd = os.open(temporary, os.O_WRONLY|os.O_CREAT|os.O_TRUNC, 0o600)
            with os.fdopen(fd,'w',encoding='utf8') as stream: json.dump({'mode': mode},stream)
            os.replace(temporary,self.settings)
        except OSError: self.message('设置保存失败；本次运行仍会使用当前选择。')
    def main_call(self, func, cancelled):
        future = concurrent.futures.Future()
        def run():
            if cancelled(): future.cancel(); return False
            try: future.set_result(func())
            except Exception as e: future.set_exception(e)
            return False
        GLib.idle_add(run)
        while not future.done():
            if cancelled(): raise GuideError('请求已取消。')
            time.sleep(.03)
        return future.result()
    def dispatch(self, request, cancelled):
        if request['command'] == 'codex_wait':
            g = self.main_call(lambda: self.runtime.require(request), cancelled)
            result = self.runtime.wait(g, request['afterEvent'], request.get('timeoutSeconds',20), cancelled)
            if result.get('event') and result['event']['kind'] in ('clicked','hover_changed') and not result['alreadyDelivered'] and not result['terminal']:
                try:
                    view = self.main_call(lambda: self.runtime.read(g, True, cancelled), cancelled)
                    result['images'] = view.pop('images', []); result['view'] = view
                except Exception as e: result['viewError'] = str(e)
            return result
        return self.main_call(lambda: self.runtime.handle(request, cancelled), cancelled)
    def tick(self):
        try:
            _, escape = self.backend.input_state()
            if escape and not self.last_escape: self.runtime.pause('已按 Esc 清除。', True)
            self.last_escape = escape
            g = self.runtime.guide
            if not g or not g.frame: return True
            frame = g.frame
            if not self.runtime.front(g.target) or not self.backend.unchanged(frame['shot']):
                self.runtime.pause('目标切换、移动或消失，请重新读取。'); return True
            pointer = self.backend.root.query_pointer(); buttons = pointer.mask & 0x100
            b = frame['bounds']; inside = b.x <= pointer.root_x < b.right and b.y <= pointer.root_y < b.bottom
            if buttons and not self.last_buttons: frame['down'] = (inside, time.monotonic(), pointer.root_x, pointer.root_y)
            if not buttons and self.last_buttons and frame['down']:
                down = frame['down']; frame['down'] = None
                if down[0] and inside and time.monotonic()-down[1] < 1.5 and abs(pointer.root_x-down[2])+abs(pointer.root_y-down[3]) <= 8:
                    with self.backend.hidden(): valid = self.backend.hit_belongs(frame['shot'], b)
                    if valid: self.runtime.observe('clicked')
            self.last_buttons = buttons
            if g.frame and inside and frame['action'] == 'open_entry' and time.monotonic()-frame['since'] > .5 and time.monotonic()-frame['hover_at'] > .8 and frame['probes'] < 3:
                frame['hover_at'] = time.monotonic(); frame['probes'] += 1
                if frame['shot'].pixels is not None:
                    from PIL import ImageChops, ImageStat
                    fresh = self.backend.capture(g.target, read_controls=False)
                    if fresh.crop != frame['shot'].crop or fresh.masks != frame['shot'].masks:
                        self.runtime.observe('hover_changed')
                    elif sum(ImageStat.Stat(ImageChops.difference(fresh.pixels, frame['shot'].pixels)).mean)/3 > 2:
                        self.runtime.observe('hover_changed')
        except Exception:
            if self.runtime.guide and self.runtime.guide.frame: self.runtime.pause('当前界面无法核实，请重读。')
        return True
    def quit(self, *_):
        self.server.close(); self.overlay.destroy(); self.backend.close(); Gtk.main_quit()

def run():
    Host(); Gtk.main(); return 0
