#!/bin/bash
# Diagnostic fallback for Macs on which the GPU backend never shows a window.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
exec /bin/bash "$project_dir/Start-SoundPreviewer.command" --software-rendering "$@"
