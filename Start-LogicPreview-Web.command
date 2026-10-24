#!/bin/bash
# Build the Mac worker once and serve the local web interface on port 5002.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
[[ "$(uname -s)" == Darwin ]] || { echo 'Native Logic rendering requires macOS.' >&2; exit 2; }
bash "$project_dir/scripts/Setup-LogicPreview.sh"
python_command="${SOUNDPREVIEWER_PYTHON:-python3}"
exec "$python_command" "$project_dir/scripts/logic_preview_web.py" --open --port 5002 "$@"
