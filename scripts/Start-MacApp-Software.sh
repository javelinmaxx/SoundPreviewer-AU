#!/bin/bash
# Launch an existing .app directly, retaining diagnostics when Finder shows nothing.
set -euo pipefail
if [[ $# -lt 1 ]]; then echo 'Usage: bash scripts/Start-MacApp-Software.sh "/path/SoundPreviewer.app" [options]' >&2; exit 2; fi
application="$1"
shift
executable="$application/Contents/MacOS/SoundPreviewer"
[[ -x "$executable" ]] || { echo "App executable not found or not executable: $executable" >&2; exit 2; }
export SOUNDPREVIEWER_SOFTWARE_RENDERING=1
exec "$executable" --software-rendering "$@"
