#!/bin/bash
# Choose a library, build once, then render serially to saved WAVs without playback.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
[[ "$(uname -s)" == Darwin ]] || { echo 'Logic batch rendering requires macOS.' >&2; exit 2; }
if [[ $# -eq 0 ]]; then
    library_path="$(/usr/bin/osascript -e 'POSIX path of (choose folder with prompt "Choose a folder containing Logic .patch packages")')"
    set -- "$library_path"
fi
bash "$project_dir/scripts/Setup-LogicPreview.sh"
python_command="${SOUNDPREVIEWER_PYTHON:-python3}"
exec "$python_command" "$project_dir/scripts/logic_patch_preview.py" --recursive "$@"
