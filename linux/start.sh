#!/bin/sh
set -eu
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "${SCREEN_GUIDE_PYTHON:-/usr/bin/python3}" "$HERE/guide.py" "$@"
