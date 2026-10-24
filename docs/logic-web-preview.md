# Local Logic preview web app

On a Mac, double-click `Start-LogicPreview-Web.command`, or run:

```bash
./Start-LogicPreview-Web.command
```

The launcher prepares the native worker and opens **http://localhost:5002**.
Keep its Terminal window running. Stop it with Ctrl+C. Requirements are macOS
13+, Python 3.10+, Xcode Command Line Tools, and the installed/licensed AUs and
assets required by your patches. No web framework or npm packages are needed.

Choose a `.patch` or a library folder, then choose an output folder or leave it
blank to use `artifacts/logic-preview/web/<library-name>`. Paste filesystem paths
without shell quotes or backslash escapes, or use the native Mac pickers.

Folders are searched recursively. A `.patch` package is treated as one preset;
its contents are not searched as additional patches. Saved audio preserves the
relative subfolder layout:

```text
Patches/Instrument/Pads/Warm.patch
    → chosen-output/Instrument/Pads/Warm.wav
```

Existing regular WAV files are preserved and marked **Existing**. They are not
revalidated against the current patch and are not counted as new renders. Links
are not followed during discovery. Ambiguous case/Unicode output-name collisions
are reported as failures instead of replacing or confusing previews.

The interface shows progress and per-patch errors. Completed WAVs can be played
or downloaded while the rest of the batch runs. Only one render runs at a time,
using fresh AU instances. A failure does not stop other patches. Stop cancels the
active worker and keeps completed files. Each batch saves a unique JSON report
and an incrementally flushed JSONL journal in its output folder. Reports and WAVs
remain on disk after the server exits; in-memory web sessions are not restored
after a server restart. Rendering the same folder again exposes existing WAVs
without overwriting them.

**AU chain only** is an explicit approximation: it omits uninterpreted Logic
metadata and controls. The same native-PST, MIDI-effect, multi-strip and routing
limitations as the standalone preview command still apply. It is not a generic
Logic project bounce or proof of sound equivalence with Logic.

The server binds only to `127.0.0.1`, checks its localhost Host/Origin headers,
requires a page token for write actions, and serves only audio associated with
its render sessions. It is a local tool, not an Internet-facing multi-user
service. The native file picker is available only on macOS. Other systems can
view the page, but cannot render Audio Units.

To run the server directly after setup:

```bash
python3 scripts/logic_preview_web.py --port 5002 --open
```

If port 5002 is occupied, stop the program using it or explicitly choose another
`--port`. The app does not silently switch ports.

## Batch rendering without a browser

Double-click `Render-LogicPatches.command` to choose a folder, or run:

```bash
./Render-LogicPatches.command "/Users/you/Music/Audio Music Apps/Patches" \
  --output-dir "/Users/you/Music/Rendered Previews" --au-chain-only
```

`Preview-LogicPatch.command` also accepts a folder and switches to batch mode.
Batch mode saves without playback. `--recursive` or `--output-dir` can also be
used with a single patch. `--output` remains the standalone command's single-WAV
option. Batch exit status is 0 for completed batches without render/discovery
failures, 1 if failures occurred, and 130 after cancellation. Existing-file skips
are distinct from render successes. The JSON report records unrun patches and
discovery problems separately.
