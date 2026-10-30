#!/bin/sh
set -eu
FIELD_CLAUDE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -n "${SCREEN_GUIDE_PYTHON:-}" ]; then
    FIELD_INTERPRETER=$SCREEN_GUIDE_PYTHON
elif [ -x /usr/bin/python3 ]; then
    FIELD_INTERPRETER=/usr/bin/python3
else
    FIELD_INTERPRETER=python3
fi
FIELD_CONFIG="$FIELD_CLAUDE_DIR/mcp.linux.local.json"
FIELD_PREPARE_ONLY=0
if [ "${1:-}" = '--prepare-only' ]; then
    FIELD_PREPARE_ONLY=1
    shift
fi
"$FIELD_INTERPRETER" "$FIELD_CLAUDE_DIR/configure.py" --platform linux --output "$FIELD_CONFIG" --guard-arguments "$@"
if [ "$FIELD_PREPARE_ONLY" -eq 1 ]; then
    exit 0
fi
SCREEN_GUIDE_CLAUDE_COMMAND=${SCREEN_GUIDE_CLAUDE_COMMAND:-claude}
if ! command -v "$SCREEN_GUIDE_CLAUDE_COMMAND" >/dev/null 2>&1; then
    printf '%s\n' '找不到 Claude Code。请先安装并登录，或用 SCREEN_GUIDE_CLAUDE_COMMAND 指定入口。' >&2
    exit 1
fi
printf '%s\n' '正在打开 指路助手专用 Claude Code 会话；请先运行 指路助手 X11 本体并在对话中明确目标软件。' >&2
printf '%s\n' '本启动器不修改全局 MCP、账号或模型，也不会启动麦克风。' >&2
FIELD_INSTRUCTIONS=$(cat "$FIELD_CLAUDE_DIR/GUIDE_PROMPT.md")
exec "$SCREEN_GUIDE_CLAUDE_COMMAND" --mcp-config "$FIELD_CONFIG" --strict-mcp-config --append-system-prompt "$FIELD_INSTRUCTIONS" "$@"
