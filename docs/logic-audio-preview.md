# Mac AU-chain audio preview

This developer tool adds an initial audio preview path for a single Logic `.patch`
instrument strip containing an externally hostable **AU instrument followed by up
to eight AU audio effects**. It restores embedded AU presets, plays MIDI notes
60, 84 and 36 at velocity 89, and renders a new stereo PCM16 WAV offline.

It is a standalone command, separate from the desktop app's library and rendering
queue. It does not render arbitrary Logic patches or claim a sound matches Logic.
The inspector's `rendering_supported: false` describes inspection alone; this new
command performs its own stricter planning and native rendering.

## Setup and run

Requirements: macOS 13+, Python 3.10+, Xcode Command Line Tools, and compatible
installed/licensed AU plug-ins and their sample libraries. No pip packages or
Homebrew dependencies are required. Run from the repository root:

```bash
# Compile the worker and test real Apple AUs without commercial plug-ins.
bash scripts/Setup-LogicPreview.sh --self-test

# Setup is incremental; this command builds as needed, renders, then plays.
./Preview-LogicPatch.command "/path/My Sound.patch"
```

Double-click `Preview-LogicPatch.command` to select a patch in a file picker.
If prerequisites are missing, setup prints the required action. For Apple tools:

```bash
xcode-select --install
```

If `python3` isn't the desired interpreter, set `SOUNDPREVIEWER_PYTHON` to the path
of a Python 3.10+ executable. Setup caches the compiled worker under
`.build/logic-preview` and checks source, build script, compiler, SDK and host
architecture before reusing it. `--force` rebuilds. Apple Silicon and Intel Macs
build for their current process architecture; compatible plug-ins must exist for
that architecture.

## Opaque Logic metadata

Real patches often contain internal Logic state that the inspector cannot fully
interpret. The default rejects it. To explicitly preview only the **recognized AU
chain**, omitting that metadata and uninterpreted mixer controls:

```bash
./Preview-LogicPatch.command "/path/My Sound.patch" --au-chain-only
```

The report lists approximations. Opaque blocks could contain keyboard zones,
bypass state, Smart Controls or unrecognized additional processing. This mode
does not reproduce those settings and is not a faithful full-patch bounce.
Even this mode rejects recognized native PST states, MIDI effects, multiple
strips/track stacks, bus routing, populated sends, muted strips, conflicting AU
identities and unreadable structures. Audio-only patches need a source-audio
workflow and are unsupported.

Logic-native plug-ins are not substituted with similar AUs. A preset containing
native `.pst` state needs a compatible Logic-hosted path that is not implemented
here. A registered AU may still fail because of architecture, licensing, missing
assets or incompatible saved state.

## Output and options

By default, each run writes a unique WAV to `artifacts/logic-preview`. Existing
audio and source patch contents are never overwritten. For a chosen new filename:

```bash
./Preview-LogicPatch.command "/path/My Sound.patch" \
  --output "/path/previews/new-preview.wav" --no-play

python3 scripts/logic_patch_preview.py "/path/My Sound.patch" \
  --notes 60,84,36 --hold 0.5 --tail 0.35 --bpm 120 --json
```

The Python command doesn't play unless `--play` is supplied. Other options include
`--velocity`, `--sample-rate` (44100 or 48000), `--timeout` and `--worker`. A failed
playback preserves the rendered WAV. Each render uses fresh AU instances and a
private, hash-checked copy of the saved states. Plug-ins may themselves access
their configured external assets; this host is not a filesystem sandbox.

The worker supplies 4/4 tempo/beat context and splits render buffers at note-on
and note-off boundaries. It checks AU state restoration's OSStatus, rejects
unsupported bus counts, nonfinite/clipping output, silence and incomplete
rendering. The caller independently validates WAV format, duration, per-note
audibility and result provenance before publishing. No parameter-by-parameter
state equivalence, external sample integrity or Logic comparison is established.
Effect latency is not compensated and tails are explicitly bounded by `--tail`;
long delays/reverbs may need a larger value. Preview notes run on MIDI channel 1.
No audio cache is reused, because a complete dependency fingerprint is not yet
available.

## Validation

```bash
python3 -m unittest discover -s tests/scripts -p 'test_*.py'
bash scripts/Setup-LogicPreview.sh --self-test
```

Portable tests cover planning, file preservation, timeout/failure cleanup,
synthetic worker contracts, setup caching and shell argument forwarding. Compiler
and worker stubs do not validate Apple frameworks. The native self-test exports
and restores fresh Apple Sampler and low-pass states, checks the restored
cutoff, loads a generated test sample, renders audible WAVs and verifies that the filter attenuates the high
note. It requires a Mac; it is not a commercial-plug-in compatibility test.
The separate `Logic AU preview` GitHub Actions workflow runs it on Apple Silicon
and Intel runners when Actions is enabled for the repository.
