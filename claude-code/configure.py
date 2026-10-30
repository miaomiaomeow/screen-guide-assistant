"""Prepare Screen Guide Claude Code stdio configuration; never launches a model or GUI."""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from pathlib import Path, PurePath


def guard_arguments(arguments: list[str]) -> None:
    reserved = json.loads(Path(__file__).with_name("reserved-options.json").read_text(encoding="utf-8"))
    for argument in arguments:
        option = argument.split("=", 1)[0].casefold()
        if option in reserved:
            raise ValueError("Screen Guide's dedicated connection and workflow cannot be overridden: " + option +
                             ". Run claude directly for a different MCP or system prompt.")


def build_config(root: PurePath, platform: str, interpreter: str) -> dict:
    if platform == "windows":
        server = {"type": "stdio", "command": str(root / "codex-plugin" / "server" / "publish" / "ScreenGuide.Plugin.exe"), "args": []}
    elif platform == "linux":
        server = {"type": "stdio", "command": interpreter, "args": [str(root / "linux" / "guide.py"), "--mcp"]}
    else:
        raise ValueError("This adapter supports Windows and Linux only.")
    return {"mcpServers": {"screen-guide": server}}


def write_config(path: Path, value: dict) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".screen-guide-mcp-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            json.dump(value, output, ensure_ascii=False, allow_nan=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=("windows", "linux"), default="windows" if os.name == "nt" else "linux")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--guard-arguments", nargs=argparse.REMAINDER, default=[])
    args = parser.parse_args()
    try:
        guard_arguments(args.guard_arguments)
    except ValueError as error:
        parser.error(str(error))
    if sys.platform == "darwin":
        parser.error("macOS is outside this release's scope.")
    root = Path(__file__).resolve().parents[1]
    entry = root / ("codex-plugin/server/publish/ScreenGuide.Plugin.exe" if args.platform == "windows" else "linux/guide.py")
    if not entry.is_file():
        parser.error("Screen Guide entry is missing: " + str(entry))
    destination = args.output or root / "claude-code" / ("mcp." + args.platform + ".local.json")
    value = build_config(root, args.platform, sys.executable)
    write_config(destination, value)
    print(json.dumps({"prepared": True, "config": str(destination.resolve()), "server": "screen-guide",
                      "globalSettingsChanged": False}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
