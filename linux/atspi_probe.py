#!/usr/bin/env python3
"""Short-lived AT-SPI reader. No action, text-value, keyboard or mouse interfaces.

The parent enforces a hard process timeout; a hung accessibility provider cannot
leave the GTK workspace waiting forever. Private object paths never go to an agent.
"""
import json
import sys
import time


def main():
    import gi
    gi.require_version("Atspi", "2.0")
    from gi.repository import Atspi

    request = json.loads(sys.stdin.read(16384))
    pid = request["pid"]
    deadline = time.monotonic() + 1.8
    Atspi.set_timeout(300, 500)
    desktop = Atspi.get_desktop(0)
    if desktop is None:
        return dict(controls=[], partial=True, reason="accessibility_desktop_unavailable")

    def valid(node):
        states = node.get_state_set()
        return states.contains(Atspi.StateType.VISIBLE) and states.contains(Atspi.StateType.SHOWING) and not states.contains(Atspi.StateType.DEFUNCT)

    def description(node, path):
        if not valid(node): return None
        name = node.get_name() or ""
        if not name.strip() or len(name) > 160 or any(ord(c) < 32 for c in name): return None
        role = (node.get_role_name() or "").lower().replace("_", " ")
        allowed = {"push button", "button", "toggle button", "check box", "radio button", "menu item", "check menu item", "radio menu item",
                   "combo box", "page tab", "tree item", "list item", "link", "entry", "slider", "spin button", "menu"}
        if role not in allowed: return None
        component = node.get_component_iface()
        if component is None: return None
        extent = component.get_extents(Atspi.CoordType.SCREEN)
        states = node.get_state_set()
        return dict(name=name.strip(), role=role, enabled=bool(states.contains(Atspi.StateType.ENABLED) and states.contains(Atspi.StateType.SENSITIVE)),
                    bounds=[extent.x, extent.y, extent.width, extent.height], path=path)

    def matches_window(node):
        if not valid(node): return False
        role = (node.get_role_name() or "").lower()
        if role not in ("frame", "window", "dialog", "alert"): return False
        attributes = node.get_attributes() or {}
        # Some toolkits expose a native XID. It is the strongest available link.
        for key in ("window-id", "window_id", "xid", "XID"):
            if key in attributes:
                try: return int(str(attributes[key]), 0) == request["xid"]
                except (ValueError, TypeError): return False
        # SHOWING + PID alone would include independent windows of the same app.
        # Without an XID, require both the exact source title and a matching top-
        # level rectangle. Ambiguous matches below produce no controls at all.
        if not request.get("title") or node.get_name() != request["title"]: return False
        component = node.get_component_iface()
        if component is None: return False
        extent = component.get_extents(Atspi.CoordType.SCREEN)
        actual = [extent.x, extent.y, extent.width, extent.height]
        return any(all(abs(a - b) <= 2 for a, b in zip(actual, request[key])) for key in ("windowBounds", "frameBounds"))

    scopes = []
    for index in range(min(desktop.get_child_count(), 256)):
        if time.monotonic() >= deadline: raise TimeoutError()
        app = desktop.get_child_at_index(index)
        if not app or app.get_process_id() != pid: continue
        for child_index in range(min(app.get_child_count(), 256)):
            child = app.get_child_at_index(child_index)
            try:
                if child and matches_window(child): scopes.append((child, [index, child_index], 0))
            except Exception: continue
    if len(scopes) != 1:
        return dict(controls=[], partial=True, reason="atspi_window_ambiguous" if scopes else "atspi_window_unproven")

    if "path" in request:
        path = request["path"]
        if not isinstance(path, list) or not path or len(path) > 18 or any(type(i) is not int or i < 0 or i > 10000 for i in path):
            return dict(controls=[], partial=True, reason="invalid_accessibility_path")
        scope_path = scopes[0][1]
        if path[:len(scope_path)] != scope_path:
            return dict(controls=[], partial=True, reason="accessibility_window_changed")
        node = desktop.get_child_at_index(path[0])
        if node is None or node.get_process_id() != pid:
            return dict(controls=[], partial=True, reason="accessibility_app_changed")
        for index in path[1:]:
            if time.monotonic() >= deadline: raise TimeoutError()
            node = node.get_child_at_index(index)
            if node is None: return dict(controls=[], partial=True, reason="accessibility_entry_disappeared")
        item = description(node, path)
        return dict(controls=[item] if item else [], partial=False, reason="")

    stack = scopes
    controls, visited, partial = [], 0, False
    while stack and visited < 1400 and len(controls) < 160:
        if time.monotonic() >= deadline:
            partial = True
            break
        node, path, depth = stack.pop()
        visited += 1
        try:
            item = description(node, path)
            if item: controls.append(item)
            if depth < 14:
                count = node.get_child_count()
                if count > 256: partial = True
                for index in range(min(count, 256) - 1, -1, -1):
                    child = node.get_child_at_index(index)
                    if child: stack.append((child, path + [index], depth + 1))
            else: partial = True
        except Exception: partial = True
    return dict(controls=controls, partial=partial or bool(stack), reason="partial_accessibility_tree" if partial or stack else "")


if __name__ == "__main__":
    try: result = main()
    except Exception as error:
        result = dict(controls=[], partial=True, reason="atspi_" + type(error).__name__)
    print(json.dumps(result, ensure_ascii=False, separators=(",", ":")))
