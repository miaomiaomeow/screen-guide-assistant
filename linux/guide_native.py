"""Linux X11 pixels, window identity and read-only AT-SPI. Never sends input."""
from __future__ import annotations

import contextlib
import io
import json
import math
import os
import subprocess
import sys
from dataclasses import replace
from pathlib import Path

from guide_core import Control, GuideError, Rect, Snapshot, Target, token, unique_json, viewport


class X11Backend:
    def __init__(self):
        from Xlib import X, display
        from PIL import Image
        self.X, self.Image = X, Image
        self.display = display.Display()
        self.root = self.display.screen().root
        self.own_windows = lambda: []
        self.flush_gui = lambda: None
        self.overlay_clear = lambda: None
        self._atoms = {}
        self._probe = Path(__file__).with_name("atspi_probe.py")
        self.escape_keycode = self.display.keysym_to_keycode(0xFF1B)
        # This implementation explicitly handles the common TrueColor X11 format.
        # Reject an unknown visual rather than send a garbled or shifted picture.
        info = self.display.display.info
        visual = next((v for depth in self.display.screen().allowed_depths for v in depth.visuals if v.visual_id == self.display.screen().root_visual), None)
        formats = [f for f in info.pixmap_formats if f.depth == self.display.screen().root_depth]
        if not visual or (visual.red_mask, visual.green_mask, visual.blue_mask) != (0xFF0000, 0xFF00, 0xFF) or info.image_byte_order != X.LSBFirst or not formats or formats[0].bits_per_pixel != 32:
            raise GuideError("Linux首版需要常见的32位小端TrueColor X11画面格式；当前格式未支持。")

    def atom(self, name):
        if name not in self._atoms: self._atoms[name] = self.display.intern_atom(name)
        return self._atoms[name]

    def window(self, xid): return self.display.create_resource_object("window", xid)

    def prop(self, window, name):
        value = window.get_full_property(self.atom(name), self.X.AnyPropertyType)
        return value.value if value is not None else None

    def pid(self, window):
        try:
            values = self.prop(window, "_NET_WM_PID")
            return int(values[0]) if values is not None and len(values) else 0
        except Exception: return 0

    @staticmethod
    def started(pid):
        try:
            fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
            return fields[19]
        except (OSError, IndexError): return ""

    def bounds(self, window):
        geometry = window.get_geometry()
        position = self.root.translate_coords(window, 0, 0)
        return Rect(position.x, position.y, geometry.width, geometry.height)

    def visible(self, window):
        try:
            states = self.prop(window, "_NET_WM_STATE")
            return window.get_attributes().map_state == self.X.IsViewable and (states is None or self.atom("_NET_WM_STATE_HIDDEN") not in states)
        except Exception: return False

    def top_frame(self, window):
        for _ in range(16):
            tree = window.query_tree()
            if tree.parent.id == self.root.id: return window
            if tree.parent.id in (0, window.id): break
            window = tree.parent
        raise GuideError("无法核实X11窗口层级。")

    def identity(self, window):
        pid = self.pid(window)
        started = self.started(pid)
        if pid <= 0 or pid == os.getpid() or not started or not self.visible(window): raise GuideError("窗口不可用。")
        klass = window.get_wm_class() or ()
        class_name = " / ".join(str(v) for v in klass)[:180]
        try: app = Path(f"/proc/{pid}/comm").read_text().strip()[:80]
        except OSError: app = class_name[:80] or "Linux应用"
        title = self.prop(window, "_NET_WM_NAME")
        if isinstance(title, bytes): title = title.decode("utf-8", "replace")
        if title is None: title = window.get_wm_name() or ""
        title = "".join(c for c in str(title) if ord(c) >= 32)[:180]
        return Target(window.id, pid, started, class_name, app, title, self.bounds(window))

    def current_target(self, target):
        try:
            current = self.identity(self.window(target.xid))
            if (current.pid, current.started, current.class_name) != (target.pid, target.started, target.class_name):
                raise GuideError("目标窗口身份已经变化，请移除后由你重新挂入。")
            return current
        except GuideError: raise
        except Exception as error: raise GuideError("目标窗口已关闭、最小化或不可读取。") from error

    def list_windows(self):
        values = self.prop(self.root, "_NET_CLIENT_LIST_STACKING")
        if values is None: values = self.prop(self.root, "_NET_CLIENT_LIST")
        if values is None: raise GuideError("窗口管理器没有提供EWMH窗口清单。")
        targets = []
        for xid in list(values)[-128:]:
            try:
                target = self.identity(self.window(int(xid)))
                if target.bounds.width >= 80 and target.bounds.height >= 60: targets.append(target)
            except Exception: continue
        return list(reversed(targets))

    def associated(self, window, target):
        if window.id == target.xid: return True
        try:
            transient = window.get_wm_transient_for()
            explicit_owner = transient is not None
            for _ in range(8):
                if transient is None: break
                if transient.id == target.xid: return self.pid(window) in (0, target.pid)
                transient = transient.get_wm_transient_for()
            if explicit_owner: return False
            types = self.prop(window, "_NET_WM_WINDOW_TYPE")
            menu_types = {self.atom("_NET_WM_WINDOW_TYPE_" + kind) for kind in ("MENU", "DROPDOWN_MENU", "POPUP_MENU", "TOOLTIP")}
            if self.pid(window) != target.pid or types is None or not any(int(t) in menu_types for t in types): return False
            # An ownerless menu can only be attributed when this PID has exactly
            # one independent EWMH client and that exact client is foreground.
            clients = self.prop(self.root, "_NET_CLIENT_LIST")
            active = self.prop(self.root, "_NET_ACTIVE_WINDOW")
            if clients is None or active is None or len(active) != 1 or int(active[0]) != target.xid: return False
            independent = []
            for xid in clients:
                client = self.window(int(xid))
                if self.pid(client) == target.pid and client.get_wm_transient_for() is None: independent.append(int(xid))
            return independent == [target.xid]
        except Exception: return False

    def foreground(self, target):
        try:
            values = self.prop(self.root, "_NET_ACTIVE_WINDOW")
            return values is not None and len(values) > 0 and self.associated(self.window(int(values[0])), target)
        except Exception: return False

    @contextlib.contextmanager
    def hidden(self):
        windows = [w for w in self.own_windows() if w.get_visible()]
        try:
            for window in windows: window.hide()
            self.flush_gui()
            self.display.sync()
            yield
        finally:
            # GTK focus-on-map is disabled; restoring 屏幕指路 never activates the target.
            for window in windows:
                if window.get_realized(): window.show()
            self.flush_gui()
            self.display.flush()

    def desktop(self):
        geometry = self.root.get_geometry()
        return Rect(0, 0, geometry.width, geometry.height)

    def layers(self, target, crop=None):
        frame = self.top_frame(self.window(target.xid))
        children = self.root.query_tree().children  # Actual bottom-to-top root stack.
        index = next((i for i, w in enumerate(children) if w.id == frame.id), None)
        if index is None: raise GuideError("无法核实目标窗口堆叠。")
        above = children[index + 1:]
        allowed, masks = [], []
        for window in above:
            if not self.visible(window): continue
            attributes = window.get_attributes()
            if getattr(attributes, "win_class", getattr(attributes, "class", None)) == self.X.InputOnly: continue
            opacity = self.prop(window, "_NET_WM_WINDOW_OPACITY")
            if opacity is not None and len(opacity) and int(opacity[0]) == 0: continue
            bounds = self.bounds(window)
            if not bounds.valid: continue
            if self.associated(window, target): allowed.append(bounds)
            else:
                # WM decoration frames usually have no PID. Their real rectangle
                # still occludes pixels; skipping all pid-less windows would leak.
                if crop is None or crop.intersects(bounds): masks.append(bounds)
        return allowed, masks

    @staticmethod
    def outside(area, cut):
        overlap = area.intersection(cut)
        if overlap is None: return [area]
        result = []
        if overlap.y > area.y: result.append(Rect(area.x, area.y, area.width, overlap.y - area.y))
        if overlap.bottom < area.bottom: result.append(Rect(area.x, overlap.bottom, area.width, area.bottom - overlap.bottom))
        if overlap.x > area.x: result.append(Rect(area.x, overlap.y, overlap.x - area.x, overlap.height))
        if overlap.right < area.right: result.append(Rect(overlap.right, overlap.y, area.right - overlap.right, overlap.height))
        return result

    def root_pixels(self, bounds):
        bounds = bounds.integral()
        if not bounds.valid or bounds.width * bounds.height > 16_000_000 or not self.desktop().contains(bounds): raise GuideError("截图范围不可用或过大。")
        raw = self.root.get_image(int(bounds.x), int(bounds.y), int(bounds.width), int(bounds.height), self.X.ZPixmap, 0xFFFFFFFF)
        if raw is None: raise GuideError("X11桌面图像读取失败。")
        width, height = int(bounds.width), int(bounds.height)
        if len(raw.data) != width * height * 4: raise GuideError("未知X11像素行格式，未发送图片。")
        return self.Image.frombytes("RGB", (width, height), raw.data, "raw", "BGRX", width * 4, 1)

    def probe(self, target, path=None):
        current = self.current_target(target)
        request = dict(pid=target.pid, xid=target.xid, title=current.title,
                       windowBounds=[current.bounds.x, current.bounds.y, current.bounds.width, current.bounds.height])
        frame = self.bounds(self.top_frame(self.window(target.xid)))
        request["frameBounds"] = [frame.x, frame.y, frame.width, frame.height]
        if path is not None: request["path"] = list(path)
        try:
            result = subprocess.run([sys.executable, str(self._probe)], input=json.dumps(request), text=True, encoding="utf-8",
                                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=2.6, check=False)
            if len(result.stdout) > 256000 or result.returncode != 0: raise GuideError("AT-SPI读取不可用。")
            result = unique_json(result.stdout)
            if not isinstance(result, dict) or not isinstance(result.get("controls"), list): raise GuideError("AT-SPI响应无效。")
            return result
        except subprocess.TimeoutExpired: return dict(controls=[], partial=True, reason="atspi_timeout")
        except Exception: return dict(controls=[], partial=True, reason="atspi_unavailable")

    def encode(self, pixels):
        view = viewport(*pixels.size)
        canvas = self.Image.new("RGB", (1000, 1000), (96, 96, 96))
        canvas.paste(pixels.resize((int(view.width), int(view.height)), self.Image.Resampling.BILINEAR), (int(view.x), int(view.y)))
        stream = io.BytesIO()
        canvas.save(stream, format="JPEG", quality=90)
        return stream.getvalue()

    def capture_controls(self, target):
        current = self.current_target(target)
        evidence = self.probe(current)
        with self.hidden():
            _, masks = self.layers(current, current.bounds)
        controls = []
        for item in evidence["controls"][:160]:
            try:
                bounds = Rect(*item["bounds"])
                if bounds.valid and current.bounds.contains(bounds) and not any(bounds.intersects(m) for m in masks):
                    controls.append(Control(token(), item["name"], item["role"], bool(item["enabled"]), bounds, tuple(item["path"])))
            except (KeyError, TypeError): continue
        if self.current_target(current).bounds != current.bounds: raise GuideError("控件读取时窗口已移动。")
        return Snapshot(token(), current, current.bounds, current.bounds, b"", tuple(controls), tuple(masks), partial=bool(evidence.get("partial")), read_note=evidence.get("reason", ""))

    def capture(self, target, focus=None, read_controls=True):
        current = self.current_target(target)
        window = current.bounds
        with self.hidden():
            popups, masks = self.layers(target)
            crop = window
            for popup in popups: crop = crop.union(popup)
            crop = crop.intersection(self.desktop())
            if crop is None: raise GuideError("目标不在可见桌面内。")
            if focus:
                desired = Rect(window.x + focus.x * window.width, window.y + focus.y * window.height, focus.width * window.width, focus.height * window.height)
                crop = crop.intersection(desired)
                if crop is None: raise GuideError("圈区已不在可见现场。")
            crop = crop.integral()
            if not crop.valid or crop.width > 8192 or crop.height > 8192 or crop.width * crop.height > 16_000_000: raise GuideError("截图区域无效或过大。")
            pixels = self.root_pixels(crop)
            after_popups, after_masks = self.layers(target, crop)
            masks += after_masks
            # Do not include desktop gaps between the root and extending menus.
            remaining = [crop]
            for allowed in [window] + popups + after_popups:
                remaining = [part for region in remaining for part in self.outside(region, allowed)]
            masks = [r.intersection(crop) for r in masks + remaining]
            masks = list(dict.fromkeys(r for r in masks if r is not None))
            if any(mask.width * mask.height >= crop.width * crop.height * .35 for mask in masks):
                raise GuideError("目标被其他窗口大块遮挡；请完整露出这个已挂窗口后重读。")
            from PIL import ImageDraw
            drawing = ImageDraw.Draw(pixels)
            for mask in masks:
                drawing.rectangle((math.floor(mask.x - crop.x), math.floor(mask.y - crop.y), math.ceil(mask.right - crop.x) - 1, math.ceil(mask.bottom - crop.y) - 1), fill=(96, 96, 96))
            evidence = self.probe(current) if read_controls else dict(controls=[], partial=False, reason="")
            controls = []
            for item in evidence["controls"][:160]:
                try:
                    bounds = Rect(*item["bounds"])
                    if bounds.valid and crop.contains(bounds) and not any(bounds.intersects(mask) for mask in masks):
                        controls.append(Control(token(), item["name"], item["role"], bool(item["enabled"]), bounds, tuple(item["path"])))
                except (KeyError, TypeError): continue
            if self.current_target(target).bounds != window: raise GuideError("读取期间窗口已移动，请重读。")
            return Snapshot(token(), current, window, crop, self.encode(pixels), tuple(controls), tuple(masks), partial=bool(evidence.get("partial")), read_note=str(evidence.get("reason", ""))[:80], pixels=pixels)

    def focus(self, shot, fractions):
        desired = Rect(shot.window.x + fractions.x * shot.window.width, shot.window.y + fractions.y * shot.window.height,
                       fractions.width * shot.window.width, fractions.height * shot.window.height).integral()
        region = desired.intersection(shot.crop)
        if region is None or not region.valid: raise GuideError("圈区不在当前图片内。")
        region = region.integral()
        pixels = shot.pixels.crop((int(region.x - shot.crop.x), int(region.y - shot.crop.y), int(region.right - shot.crop.x), int(region.bottom - shot.crop.y)))
        return replace(shot, id=token(), crop=region, jpeg=self.encode(pixels), controls=tuple(c for c in shot.controls if region.contains(c.bounds)),
                       masks=tuple(m for m in shot.masks if region.intersects(m)), pixels=pixels)

    def unchanged(self, shot):
        try: return self.current_target(shot.target).bounds == shot.window
        except Exception: return False

    def hit_belongs(self, shot, bounds):
        popups, masks = self.layers(shot.target, bounds)
        return not any(bounds.intersects(mask) for mask in masks) and any(region.contains(bounds) for region in [shot.window] + popups)

    def confirm_control(self, shot, control):
        if not self.unchanged(shot): return False
        evidence = self.probe(shot.target, control.path)
        if len(evidence["controls"]) != 1: return False
        current = evidence["controls"][0]
        if (current.get("name"), current.get("role"), current.get("enabled")) != (control.name, control.role, True): return False
        try: same = Rect(*current["bounds"]) == control.bounds
        except (KeyError, TypeError): return False
        with self.hidden(): return same and self.hit_belongs(shot, control.bounds)

    def confirm_image(self, shot, bounds):
        if not self.unchanged(shot): return False
        with self.hidden():
            if not self.hit_belongs(shot, bounds): return False
            current = self.root_pixels(bounds)
            previous = shot.pixels.crop((int(bounds.x - shot.crop.x), int(bounds.y - shot.crop.y), int(bounds.right - shot.crop.x), int(bounds.bottom - shot.crop.y)))
            # Similar sampled pixels and a bounded mean difference, not just a hash
            # of one point or a coordinate check. Animated content may safely refuse.
            from PIL import ImageChops, ImageStat
            difference = ImageStat.Stat(ImageChops.difference(previous, current))
            return sum(difference.mean) / 3 <= 10 and self.unchanged(shot)

    def clear_overlay(self): self.overlay_clear()

    def input_state(self):
        # Read only a transient pressed/not-pressed state. Never retain key values,
        # decode typed text, grab input or send synthetic events to any application.
        keys = self.display.query_keymap()
        code = self.escape_keycode
        escape = bool(code and keys[code // 8] & (1 << (code % 8)))
        buttons = self.root.query_pointer().mask & 0x1F00
        return bool(any(keys) or buttons), escape

    def close(self): self.display.close()
