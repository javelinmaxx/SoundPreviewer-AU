#!/bin/bash
# Build for this Mac's architecture; no Homebrew, pip packages, or commercial AUs.
set -euo pipefail
project_dir="$(cd -- "$(dirname -- "$0")/.." && pwd)"
[[ "$(uname -s)" == Darwin ]] || { echo 'Logic audio preview requires macOS. The inspector works on other platforms.' >&2; exit 2; }
self_test=0
force=0
for option in "$@"; do
    case "$option" in
        --self-test) self_test=1 ;;
        --force) force=1 ;;
        *) echo "Usage: bash scripts/Setup-LogicPreview.sh [--self-test] [--force]" >&2; exit 2 ;;
    esac
done
python_command="${SOUNDPREVIEWER_PYTHON:-python3}"
command -v "$python_command" >/dev/null 2>&1 || { echo 'Install Python 3.10+ or set SOUNDPREVIEWER_PYTHON to its executable.' >&2; exit 2; }
"$python_command" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else "Python 3.10+ is required.")'
compiler="$(xcrun --sdk macosx --find swiftc 2>/dev/null)" || { echo 'Install Xcode Command Line Tools: xcode-select --install' >&2; exit 2; }
sdk="$(xcrun --sdk macosx --show-sdk-path)"
architecture="$(uname -m)"
case "$architecture" in arm64|x86_64) ;; *) echo "Unsupported Mac architecture: $architecture" >&2; exit 2 ;; esac
build_dir="$project_dir/.build/logic-preview"
[[ ! -L "$project_dir/.build" && ! -L "$build_dir" ]] || { echo 'Refusing a linked native build directory.' >&2; exit 2; }
mkdir -p "$build_dir"
source_file="$project_dir/src/SoundPreviewer.LogicPreview/main.swift"
fingerprint="$("$python_command" - "$source_file" "$compiler" "$sdk" "$architecture" "$project_dir/scripts/Setup-LogicPreview.sh" <<'PY'
import hashlib, pathlib, subprocess, sys
source, compiler, sdk, architecture, setup = sys.argv[1:]
digest = hashlib.sha256(pathlib.Path(source).read_bytes())
digest.update(pathlib.Path(setup).read_bytes())
for value in (compiler, sdk, architecture, subprocess.check_output([compiler, '--version'], text=True)):
    digest.update(value.encode()); digest.update(b'\0')
print(digest.hexdigest())
PY
)"
previous=""
if [[ -f "$build_dir/build.sha256" ]]; then previous="$(cat "$build_dir/build.sha256")"; fi
if [[ "$force" == 1 || ! -x "$build_dir/LogicPreview" || "$fingerprint" != "$previous" ]]; then
    echo "Building Logic AU preview worker for $architecture (macOS 13+)..." >&2
    temporary="$(mktemp -d "$build_dir/compile.XXXXXX")"
    trap 'rm -rf -- "$temporary"' EXIT
    "$compiler" -swift-version 5 -O -sdk "$sdk" -target "$architecture-apple-macosx13.0" \
        -module-cache-path "$build_dir/module-cache" -framework AVFoundation -framework AudioToolbox \
        "$source_file" -o "$temporary/LogicPreview"
    mv -f -- "$temporary/LogicPreview" "$build_dir/LogicPreview"
    printf '%s\n' "$fingerprint" > "$temporary/build.sha256"
    mv -f -- "$temporary/build.sha256" "$build_dir/build.sha256"
else
    echo "Logic AU preview worker is current ($architecture)." >&2
fi
if [[ "$self_test" == 1 ]]; then
    # The test restores fresh Apple Sampler/low-pass instances and validates PCM.
    "$python_command" - "$build_dir/LogicPreview" <<'PY'
import subprocess, sys
subprocess.run([sys.argv[1], '--self-test'], check=True, timeout=180)
PY
fi
echo 'Ready. Run ./Preview-LogicPatch.command "/path/My Sound.patch"' >&2
