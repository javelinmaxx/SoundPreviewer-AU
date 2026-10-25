# Render Logic Patch files in the desktop app

Added in 1.4.0. This connects the existing native Logic AU-chain previewer to
the desktop library, render queue, saved previews and playback controls.

## Use on macOS

1. Pull the latest source and rebuild the desktop app. An older .app binary
   does not gain this feature from `git pull` alone.
2. In preview generation settings, click **准备 Logic Patch 环境**. This compiles
   the native AU worker into the application's Preview data directory under
   `renderers/logic-preview`, without writing build output inside the .app.
   macOS 13+, Python 3.10+ and Xcode Command Line Tools are required. Install
   the tools with `xcode-select --install` if needed.
3. Add the folder containing your patches and choose **Logic Patch** under
   **音源**, then scan. Automatic mixed-library detection also recognizes patches.
   A `.patch` package itself may be used as a source directory.
4. Select the indexed sounds and use the existing preview generation controls.
   WAV or AAC storage, the three configured pitches, timing, quick previews,
   cancellation and playback use the existing desktop workflow.

The default preparation selects a serial Logic Patch backend. It does not
change other synthesizers' settings or remove existing previews. It uses
`SOUNDPREVIEWER_PYTHON` if set; otherwise it checks standard Homebrew Python
locations before Apple's Python. Missing dependencies produce an actionable
error rather than substituted audio.

## Supported scope and explicit approximation

One `.patch` package becomes one library sound. The worker restores a supported
single instrument AU and its ordered effect AUs using the existing checked
planner. This does not claim equivalence to a complete Logic channel strip.
Logic-native PST instruments, MIDI effects, unsupported sends and multi-strip
stacks remain rejected. Plugin licenses, installed resources and matching AU
architecture are still required. Discovery alone does not establish loadability.

**Logic AU 链近似** is disabled by default. For a supported AU chain with
uninterpreted channel metadata, explicitly enable that checkbox on the Logic
Patch row and save settings before rendering. This permits the same bounded
approximation as CLI `--au-chain-only`; it does not bypass unsupported plugin
or routing checks. Results are marked for review.

All package files contribute to the indexed content fingerprint; internal
files are not separately indexed as presets. Source changes before/during a
render reject the result and require rescanning. The native worker, Python
bridge, inspector, renderer and approximation policy contribute to the render
cache identity. External sample files and installed AU updates are not fully
fingerprinted; regenerate previews after changing those dependencies.

## Packaging and validation

Normal .NET build/publish copies the Python scripts, setup script and Swift
source under `Renderers/LogicPatch`. Runtime preparation compiles for the current
Mac; users do not need to modify the app bundle. The Logic preview CI workflow
checks Intel and Apple Silicon: it wraps exported real Apple Sampler and low-pass
states in a synthetic patch container and runs desktop scanning, the separate
.NET worker, Python planner, native AU rendering, fixed and quick note mapping,
and committed WAV validation. Synthetic fixture contracts also run on Linux;
they do not prove compatibility with third-party presets.
