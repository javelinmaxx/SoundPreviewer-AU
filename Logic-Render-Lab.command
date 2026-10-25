#!/bin/bash
# Experimental helpers only: default action inspects Logic without launching it.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
python_command="${SOUNDPREVIEWER_PYTHON:-python3}"
"$python_command" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else "Python 3.10+ is required.")'
if [[ $# == 0 ]]; then set -- probe; fi
exec "$python_command" "$project_dir/scripts/logic_render_lab.py" "$@"
