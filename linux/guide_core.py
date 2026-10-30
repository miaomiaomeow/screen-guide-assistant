"""Bounded pointing state; the host agent decides, the human operates the app."""
from __future__ import annotations

import base64
import copy
import io
import json
import math
import os
import tempfile
import threading
import time
import uuid
import zipfile
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

VERSION = "0.1.0-guide-x11"
MAX_REQUEST = 131072
MAX_RESPONSE = 24 * 1024 * 1024
COORDINATES = "1000x1000 image pixels, [left,top,right,bottom], includes letterbox. Grey padding and masked regions are not targets."


class GuideError(ValueError):
    pass


def token() -> str:
    return uuid.uuid4().hex


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def number(value: Any) -> bool:
    try:
        return type(value) in (int, float) and math.isfinite(value)
    except OverflowError:
        return False


def text(value: Any, maximum: int = 80) -> str:
    if not isinstance(value, str) or not 1 <= len(value.strip()) <= maximum or len(value) > maximum:
        raise GuideError("文字为空、格式不符或超过长度限制。")
    if any(ord(c) < 32 and c not in "\r\n\t" for c in value):
        raise GuideError("文字含不可见控制字符。")
    return value.strip()


def unique_json(data: str) -> Any:
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise GuideError("JSON含重复字段。")
            result[key] = value
        return result
    def bad_constant(_):
        raise GuideError("JSON数字必须有限。")
    try:
        return json.loads(data, object_pairs_hook=pairs, parse_constant=bad_constant)
    except (ValueError, RecursionError) as error:
        raise GuideError("JSON格式无效或过深。") from error


@dataclass(frozen=True)
class Rect:
    x: float
    y: float
    width: float
    height: float

    @property
    def right(self): return self.x + self.width

    @property
    def bottom(self): return self.y + self.height

    @property
    def valid(self): return all(number(v) for v in (self.x, self.y, self.width, self.height)) and self.width > 0 and self.height > 0

    def edges(self): return [self.x, self.y, self.right, self.bottom]

    def contains(self, other):
        return self.valid and other.valid and self.x <= other.x and self.y <= other.y and self.right >= other.right and self.bottom >= other.bottom

    def intersects(self, other):
        return max(self.x, other.x) < min(self.right, other.right) and max(self.y, other.y) < min(self.bottom, other.bottom)

    def intersection(self, other):
        left, top = max(self.x, other.x), max(self.y, other.y)
        right, bottom = min(self.right, other.right), min(self.bottom, other.bottom)
        return Rect(left, top, right - left, bottom - top) if right > left and bottom > top else None

    def union(self, other):
        left, top = min(self.x, other.x), min(self.y, other.y)
        return Rect(left, top, max(self.right, other.right) - left, max(self.bottom, other.bottom) - top)

    def integral(self):
        left, top = math.ceil(self.x), math.ceil(self.y)
        return Rect(left, top, max(0, math.floor(self.right) - left), max(0, math.floor(self.bottom) - top))


def viewport(width: int, height: int) -> Rect:
    if width <= 0 or height <= 0:
        raise GuideError("图片尺寸无效。")
    scale = min(1000 / width, 1000 / height)
    w, h = max(1, math.floor(width * scale + .5)), max(1, math.floor(height * scale + .5))
    return Rect((1000 - w) // 2, (1000 - h) // 2, w, h)


@dataclass(frozen=True)
class Target:
    xid: int
    pid: int
    started: str
    class_name: str
    app: str
    title: str
    bounds: Rect

    @property
    def caption(self): return self.app + (" · " + self.title if self.title else "")


@dataclass(frozen=True)
class Control:
    id: str
    name: str
    role: str
    enabled: bool
    bounds: Rect
    path: tuple[int, ...] = ()

    def metadata(self):
        return dict(id=self.id, name=self.name, role=self.role, context="", enabled=self.enabled)


@dataclass(frozen=True)
class Snapshot:
    id: str
    target: Target
    window: Rect
    crop: Rect
    jpeg: bytes
    controls: tuple[Control, ...] = ()
    masks: tuple[Rect, ...] = ()
    created: str = field(default_factory=utc_now)
    monotonic: float = field(default_factory=time.monotonic)
    partial: bool = False
    read_note: str = ""
    pixels: Any = None


def map_box(shot: Snapshot, values: Any, max_fraction: float = .25) -> Rect:
    if not isinstance(values, list) or len(values) != 4 or not all(number(v) and 0 <= v <= 1000 for v in values):
        raise GuideError("box须为当前图片中的四个0..1000有限数值。")
    left, top, right, bottom = values
    box = Rect(left, top, right - left, bottom - top)
    view = viewport(int(shot.crop.width), int(shot.crop.height))
    if not box.valid or not view.contains(box):
        raise GuideError("图片框在补边、画面外或没有面积。")
    bounds = Rect(shot.crop.x + (left - view.x) * shot.crop.width / view.width,
                  shot.crop.y + (top - view.y) * shot.crop.height / view.height,
                  box.width * shot.crop.width / view.width, box.height * shot.crop.height / view.height).integral()
    if not bounds.valid or bounds.width < 8 or bounds.height < 8 or bounds.width * bounds.height > shot.crop.width * shot.crop.height * max_fraction:
        raise GuideError("图片框过小或过大。")
    if not shot.crop.contains(bounds) or any(bounds.intersects(mask) for mask in shot.masks):
        raise GuideError("图片框在遮挡区域。")
    return bounds


def image_box(shot: Snapshot, bounds: Rect) -> list[float]:
    view = viewport(int(shot.crop.width), int(shot.crop.height))
    return [view.x + (bounds.x - shot.crop.x) * view.width / shot.crop.width,
            view.y + (bounds.y - shot.crop.y) * view.height / shot.crop.height,
            view.x + (bounds.right - shot.crop.x) * view.width / shot.crop.width,
            view.y + (bounds.bottom - shot.crop.y) * view.height / shot.crop.height]


COMMANDS = dict(screen_guide_status="status", screen_guide_list_targets="list_targets",
                screen_guide_bind_target="bind_target", screen_guide_guide="codex_start",
                screen_guide_read_view="codex_read", screen_guide_point="codex_point",
                screen_guide_wait_event="codex_wait", screen_guide_clear="clear")
MODES = ("hybrid", "controls", "direct_image", "image_on_failure")


def tool_catalog():
    return json.loads(Path(__file__).with_name("tools.json").read_text(encoding="utf-8"))["tools"]


def validate_request(tool, arguments):
    if tool not in COMMANDS or not isinstance(arguments, dict): raise GuideError("工具或参数无效。")
    schema = next(t["inputSchema"] for t in tool_catalog() if t["name"] == tool)
    def validate(value, shape):
        kind = shape.get("type")
        valid = {"object": isinstance(value, dict), "array": isinstance(value, list), "string": isinstance(value, str),
                 "number": number(value), "integer": type(value) is int, "boolean": type(value) is bool}.get(kind, True)
        if not valid: raise GuideError("参数类型不符。")
        if "enum" in shape and value not in shape["enum"]: raise GuideError("参数选项无效。")
        if kind == "object":
            props = shape.get("properties", {})
            if value.keys() - props.keys() or set(shape.get("required", [])) - value.keys(): raise GuideError("参数缺失或含未知字段。")
            for name, part in value.items(): validate(part, props[name])
        elif kind == "array":
            if not shape.get("minItems", 0) <= len(value) <= shape.get("maxItems", 1000): raise GuideError("数组长度无效。")
            for part in value: validate(part, shape["items"])
        elif kind == "string":
            text(value, shape.get("maxLength", 500))
            if len(value) < shape.get("minLength", 0) or any(ord(c) < 32 for c in value): raise GuideError("文字格式无效。")
        elif kind in ("number", "integer"):
            if not number(value) or not shape.get("minimum", -1e100) <= value <= shape.get("maximum", 1e100): raise GuideError("数字超出范围。")
    validate(arguments, schema)
    if tool == "screen_guide_point":
        action = arguments["action"]
        locations = int("candidateId" in arguments) + int("box" in arguments)
        if (action in ("point", "open_entry") and locations != 1) or (action not in ("point", "open_entry") and locations):
            raise GuideError("只能选择本次控件ID或图片框之一。")
        if "box" in arguments:
            box = arguments["box"]
            if not all(0 <= v <= 1000 for v in box) or box[2] <= box[0] or box[3] <= box[1]: raise GuideError("图片框无效。")
    return dict(arguments, command=COMMANDS[tool])


@dataclass
class Guide:
    question: str
    target: Target
    mode: str
    id: str = field(default_factory=token)
    memory: list = field(default_factory=list)
    image_fallback: bool = False
    shot: Snapshot | None = None
    frame: dict | None = None
    paused: bool = True
    terminal: bool = False
    cursor: int = 0
    delivered: int = 0
    events: list = field(default_factory=list)
    automatic: bool = False

    @property
    def images(self): return self.mode in ("hybrid", "direct_image") or self.image_fallback


class GuideRuntime:
    def __init__(self, backend, changed=lambda message: None):
        self.backend, self.changed = backend, changed
        self.targets, self.bound, self.guide = {}, None, None
        self.preferred_mode = "hybrid"
        self.condition = threading.Condition()

    def front(self, target):
        return self.backend.foreground(target) or getattr(self.backend, 'own_foreground', lambda: False)()

    def emit(self, guide, kind, message):
        with self.condition:
            guide.cursor += 1
            guide.events.append(dict(cursor=guide.cursor, kind=kind, message=message))
            guide.events = guide.events[-32:]
            self.condition.notify_all()

    def status(self):
        g = self.guide
        return dict(ok=True, version=VERSION, platform="linux-x11", screenshotMode=g.mode if g else self.preferred_mode,
                    preferredMode=self.preferred_mode, microphoneOn=False, decisionProvider="current_agent",
                    boundTarget=None if self.bound is None else dict(app=self.bound.app),
                    session=None if g is None else dict(sessionId=g.id, cursor=g.cursor, step=len(g.memory), terminal=g.terminal, paused=g.paused))

    def pause(self, message, terminal=False):
        self.backend.clear_overlay()
        g = self.guide
        if g and not g.terminal:
            g.frame = g.shot = None; g.paused = True; g.terminal = terminal
            self.emit(g, "cancelled" if terminal else "paused", message)
        self.changed(message)

    def require(self, request):
        g = self.guide
        if g is None or request.get("sessionId") != g.id: raise GuideError("会话已失效，请重新开始。")
        return g

    def handle(self, request, cancelled=lambda: False):
        if cancelled(): raise GuideError("请求已取消。")
        command = request["command"]
        if command == "status": return self.status()
        if command == "clear": self.pause("已清除高亮并结束本轮。", True); return dict(ok=True)
        if command == "list_targets":
            self.targets = {token()[:16]: t for t in self.backend.list_windows()[:48]}
            return dict(ok=True, targets=[dict(targetId=k, app=t.app, windowClass=t.class_name,
                width=t.bounds.width, height=t.bounds.height, foreground=self.backend.foreground(t)) for k,t in self.targets.items()])
        if command == "bind_target":
            if self.guide and not self.guide.terminal: raise GuideError("请先明确清除当前指路。")
            target = self.targets.get(request["targetId"])
            if target is None: raise GuideError("目标ID已过期，请重新列出窗口。")
            self.bound = self.backend.current_target(target)
            self.changed("已绑定：" + self.bound.app)
            return dict(ok=True, app=self.bound.app)
        if command == "codex_start":
            if self.guide and not self.guide.terminal: raise GuideError("已有会话，请先清除后再开始新问题。")
            if self.bound is None: raise GuideError("请先明确绑定目标软件。")
            target = self.backend.current_target(self.bound)
            self.guide = Guide(request["question"].strip(), target, request.get("mode", self.preferred_mode))
            self.changed("请保持目标可见；当前 Agent 会读取界面并判断入口。")
            return dict(ok=True, sessionId=self.guide.id, cursor=0, mode=self.guide.mode, next="read_view", usesDesktopConfiguredModel=False)
        g = self.require(request)
        if command == "codex_wait": return self.wait(g, request["afterEvent"], request.get("timeoutSeconds", 20), cancelled)
        if g.terminal: raise GuideError("本轮已结束。")
        if not self.front(g.target):
            self.pause("请自行切回绑定软件后重读。")
            raise GuideError("目标不在前台；助手不会替你激活窗口。")
        if command == "codex_read": return self.read(g, cancelled=cancelled)
        if command == "codex_point": return self.point(g, request, cancelled)
        raise GuideError("不支持的指路操作。")

    def read(self, g, automatic=False, cancelled=lambda: False):
        if g.terminal: raise GuideError("本轮已结束。")
        self.backend.clear_overlay(); g.frame = g.shot = None
        images = g.images
        if images: shot = self.backend.capture(g.target, read_controls=g.mode == "hybrid")
        else:
            shot = self.backend.capture_controls(g.target)
            if not shot.controls and g.mode == "image_on_failure":
                g.image_fallback = images = True
                shot = self.backend.capture(g.target, read_controls=False)
        if cancelled(): raise GuideError("读取已取消，未使用旧结果。")
        if not self.front(g.target) or not self.backend.unchanged(shot): raise GuideError("读取期间目标已变化。")
        if not images and not shot.controls: raise GuideError("当前没有可靠可读的控件；当前模式不发送截图。")
        g.shot, g.automatic, g.paused = shot, automatic, False
        self.changed("已读取当前界面，等待 Agent 选择入口。")
        return dict(ok=True, sessionId=g.id, snapshotId=shot.id, cursor=g.cursor, mode=g.mode,
                    source="hybrid" if g.mode == "hybrid" else "image" if images else "controls",
                    question=g.question, controls=[dict(c.metadata(), expandable=("menu" in c.role or "combo" in c.role)) for c in shot.controls],
                    memory=dict(interactions=list(g.memory)), partial=shot.partial, controlReadNote=shot.read_note,
                    imageCoordinates=COORDINATES,
                    images=[dict(label="当前目标窗口", data=base64.b64encode(shot.jpeg).decode(), mimeType="image/jpeg")] if images else [])

    def refuse(self, g, message, fallback=False, cancelled=lambda: False):
        if fallback and g.mode == "image_on_failure" and not g.images:
            g.image_fallback = True
            return self.read(g, True, cancelled)
        self.pause(message)
        return dict(ok=False, message=message, next="read_view")

    def point(self, g, request, cancelled):
        shot = g.shot
        if shot is None or request["snapshotId"] != shot.id or time.monotonic() - shot.monotonic > 30:
            raise GuideError("快照已过期，请重新读取。")
        self.backend.clear_overlay(); g.shot = None; g.frame = None
        action, score = request["action"], request["confidence"]
        if action == "done":
            if score < .9 or not g.memory or g.memory[-1]["event"] != "clicked" or g.memory[-1]["action"] != "point":
                raise GuideError("尚无目标入口实际点击证据，不能声称完成。")
            g.terminal = True; self.emit(g, "done", "本轮指路结束。")
            self.changed("本轮指路结束；不代表软件任务已经成功。")
            return dict(ok=True, done=True, sessionId=g.id, cursor=g.cursor)
        if action in ("not_found", "ambiguous"):
            return self.refuse(g, "无法可靠确定入口，请展开相关区域或说具体些。", True, cancelled)
        control = None
        if "candidateId" in request:
            choices = [c for c in shot.controls if c.id == request["candidateId"]]
            if len(choices) != 1: raise GuideError("候选编号不属于当前快照。")
            control = choices[0]
            if not control.enabled: raise GuideError("控件已禁用，未显示高亮。")
            if sum(c.name == control.name and c.role == control.role for c in shot.controls) > 1:
                return self.refuse(g, "同名控件仍有歧义，请结合当前图片继续。", True, cancelled)
            if score < (.6 if action == "open_entry" else .85): return self.refuse(g, "选择置信度不足。", True, cancelled)
            if not self.backend.confirm_control(shot, control): raise GuideError("控件身份或边界已变化。")
            bounds, label, role = control.bounds, control.name, control.role
        else:
            if not shot.jpeg: raise GuideError("仅控件快照不接受图片坐标。")
            if score < .9: return self.refuse(g, "图片位置置信度不足。")
            bounds = map_box(shot, request["box"])
            if any(not c.enabled and c.bounds.intersects(bounds) for c in shot.controls): raise GuideError("图片框重叠禁用入口，未画框。")
            label, role = text(request.get("label", "所选入口"), 240), "ImageEntry"
            if not self.backend.confirm_image(shot, bounds): raise GuideError("图片区域已变化或被遮挡。")
        if g.automatic and g.memory and g.memory[-1]["name"] == label:
            return self.refuse(g, "仍重复刚操作过的入口，请重新读当前界面。", True, cancelled)
        if cancelled() or not self.front(g.target) or not self.backend.unchanged(shot): raise GuideError("定位期间目标已变化。")
        g.frame = dict(shot=shot, control=control, bounds=bounds, label=label, role=role, action=action,
                       since=time.monotonic(), hover_at=0.0, probes=0, down=None)
        g.paused = False
        self.backend.show_frame(g.frame)
        self.changed(("黄框：" if control else "白框估计：") + label + "。请自行操作。")
        return dict(ok=True, sessionId=g.id, cursor=g.cursor, label=label, estimated=control is None, next="wait_event")

    def observe(self, kind):
        g = self.guide
        if g is None or g.frame is None or g.terminal or g.paused: return
        frame = g.frame
        g.memory.append(dict(step=len(g.memory)+1, name=frame["label"], role=frame["role"], context="", action=frame["action"], event=kind))
        g.frame = g.shot = None; self.backend.clear_overlay()
        if len(g.memory) >= 12:
            g.terminal = True; self.emit(g, "limit", "已到12次交互上限，请缩小问题后重新开始。")
        else: self.emit(g, kind, "已观察到用户操作；请读取最新界面后再判断。")
        self.changed("已记录操作，等待当前 Agent 继续。")

    def wait(self, g, after, seconds, cancelled):
        with self.condition:
            if after > g.cursor: raise GuideError("不能使用未来事件游标。")
            until = time.monotonic() + seconds
            while after >= g.cursor and not g.terminal and time.monotonic() < until:
                if cancelled(): raise GuideError("等待已取消。")
                self.condition.wait(min(.1, max(0, until-time.monotonic())))
            events = [e for e in g.events if e["cursor"] > after]
            event = events[0] if events else None
            duplicate = bool(event and event["cursor"] <= g.delivered)
            if event: g.delivered = max(g.delivered, event["cursor"])
            return dict(ok=True, sessionId=g.id, cursor=event["cursor"] if event else g.cursor, event=event,
                        timedOut=not event and not g.terminal, terminal=g.terminal, alreadyDelivered=duplicate)


