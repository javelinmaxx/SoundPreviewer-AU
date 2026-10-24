#!/bin/bash
# Run setup incrementally, render to a new WAV, then play the validated result.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
# Preserve the caller's directory so relative patch/output paths retain their
# Terminal meaning. All repository tools below already use absolute paths.
[[ "$(uname -s)" == Darwin ]] || { echo 'Logic audio preview requires macOS.' >&2; exit 2; }
if [[ $# -eq 0 ]]; then
    patch_path="$(/usr/bin/osascript -e 'POSIX path of (choose file with prompt "Choose a Logic Library .patch package (not a .pst preset)")')"
    set -- "$patch_path"
fi
bash "$project_dir/scripts/Setup-LogicPreview.sh"
python_command="${SOUNDPREVIEWER_PYTHON:-python3}"
exec "$python_command" "$project_dir/scripts/logic_patch_preview.py" --play "$@"
