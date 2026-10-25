# Logic Pro 12.0 factory-patch rendering investigation

Branch: `logic-render`. Target supplied by the user: **Logic Pro 12.0**, factory
Library patches. This investigation adds separate command-line research tools;
it does not change the desktop renderer or its shipped version.

## Findings

**Rendering through Logic is feasible. Fully automated factory-patch selection
and bouncing on Logic 12.0 have not yet been demonstrated here.**

Apple documents selecting a Library patch for the selected track, and says that
the left inspector channel strip then shows the patch name, instrument and
effects. Apple also documents project bouncing to PCM files. A project-output
bounce is the appropriate first experiment for patches with stacks, auxes or
sends; an isolated instrument render or a region bounce may not capture their
complete output routing.

Logic's own engines can interpret their native PST states. Converting those
states into XML or an AU preset does not supply a loadable implementation of
the instruments. The existing standalone AU host therefore cannot be the
fallback for these factory patches.

No supported general-purpose headless Library-patch render API was found in
the Apple documentation reviewed. That is a research finding, not proof that
every Logic 12.0 automation interface is absent. The installed application's
scripting dictionary must be inspected before choosing an automation approach.
Generic `open`/`close` Apple Events alone would not provide patch loading or
bouncing. A `bounce`-like command, if found, also needs an actual render test.

Apple documents System Events/Accessibility UI scripting as a way to inspect
and operate applications. This is a plausible route if Logic lacks sufficient
direct scripting commands. Its feasibility depends on the **actual Logic 12.0
UI hierarchy**, language, key-command configuration, dialogs and loading state.
No unverified key sequences, screen coordinates or private Logic libraries
have been added to the production renderer.

## Tools on this branch

Use Python 3.10+; no pip packages are required. Set `SOUNDPREVIEWER_PYTHON` to
the Python executable if needed. The launcher preserves the caller's working
directory so relative template/workspace paths work normally.

```bash
git fetch origin
git switch logic-render
./Logic-Render-Lab.command probe
```

`probe` reads `/Applications/Logic Pro.app/Contents/Info.plist`, reports its
version and extracts its public dictionary with `/usr/bin/sdef`. It does not
launch Logic. A missing dictionary is reported as a diagnostic. Supply
`--application "/another/path/Logic Pro.app"` for a different installation.

For optional UI discovery, open a **new dedicated Logic test project** yourself:

```bash
./Logic-Render-Lab.command probe --accessibility
```

This reads running Logic's window titles and top-level menu labels. It does not
activate the app, select a patch, type, click, start transport or bounce. macOS
may request Automation/Accessibility permission for the terminal or script
host. A denied permission is a diagnostic; the script does not change privacy
settings or retry by driving the UI. This probe does not yet inspect Library
rows, channel strips or Bounce-dialog controls.

To list filesystem patch candidates:

```bash
./Logic-Render-Lab.command inventory
# Supply a verified location if your installation uses another folder:
./Logic-Render-Lab.command inventory --root "/path/to/installed/Patches"
```

The default candidates are the app's `Contents/Resources/Patches` and
`/Library/Application Support/Logic/Patches`. Their presence and completeness
are **not established for Logic 12.0**. Sound Library relocation/downloads and
other catalog storage may require different paths. This inventories package
names, not an authoritative list of installed or playable factory sounds.

## First native-instrument experiment

Select a factory patch in your Library and substitute its exact name below.
For duplicate names, record the category too. `Deluxe Classic` is an example
from the user's earlier patch path, not a verified Logic 12 factory catalog.

```bash
./Logic-Render-Lab.command prepare \
  --name "Deluxe Classic" \
  --workspace "$HOME/Music/SoundPreviewer-LogicLab/Deluxe-Classic-01"
```

Optionally add `--template "/path/to/Dedicated Empty.logicx"`. The tool copies
that project to the fresh workspace as `Session.logicx`. It never opens or
edits the source project. Use a small empty template: copies are limited to
256 MiB/4096 members and reject links. An existing workspace is never replaced.
No private `.logicx` internals are generated or patched.

The workspace contains:

- `Preview.mid`: standard type-0 MIDI, 960 PPQ, 120 BPM, channel 1, notes
  **60, 84, 36**, velocity 89. There are no program-change messages. Notes begin
  after a one-bar lead-in. Default hold is 0.5 s, followed by 1 s of tail per
  note. `--notes`, `--velocity`, `--bpm`, `--hold` and `--tail` are configurable.
  Drum kits or range-limited instruments may need different notes; slow-attack
  sounds may need a longer hold. Silence in a requested window is a diagnostic,
  not proof that the patch itself is broken.
- `request.json`: exact quantized MIDI timing, MIDI SHA256, requested patch
  name, target version and explicit manual/unverified status.
- `STEPS.txt`: experiment-specific Logic instructions, including duration.

Open the copied project, or create a new empty project if no template was
provided. Choose the factory patch in Logic's Library, wait for samples to load,
and retain its complete stack and routing. Import the MIDI onto its instrument
track at bar 1, preserving the one-bar lead-in. If opening the MIDI creates a
separate project, move its region into the dedicated patch project instead.

Bounce **File > Bounce > Project or Section** from bar 1 to uncompressed stereo
PCM WAV at 44.1 kHz, 16- or 24-bit integer. Disable normalization and dither;
include the audio tail. A default experiment needs at least 6.5 seconds; a later
musical end position is acceptable within the additional 10-second allowance.
Save as `Bounce.wav` in the workspace. Do not use an unrelated working project.

```bash
./Logic-Render-Lab.command collect \
  --workspace "$HOME/Music/SoundPreviewer-LogicLab/Deluxe-Classic-01" \
  --bounce "$HOME/Music/SoundPreviewer-LogicLab/Deluxe-Classic-01/Bounce.wav" \
  --confirmed-patch "Deluxe Classic"
```

`collect` checks PCM format, channels, sample rate, bounded duration, file
stability, unchanged experiment MIDI and non-silent energy in each requested
note window. It flags clipping and publishes `preview.wav` plus `result.json`
without overwriting prior files. Unsupported WAV containers should be re-exported
as standard 16-bit PCM WAV; older Python versions cannot read all extensible WAV
headers. Existing bounces and templates are preserved.

These checks **do not establish the patch identity, MIDI pitches, exact note
timing, Logic version actually used or that Logic produced the file**. A wrong
project can also have audible audio in those windows. Reports explicitly set
`preset_identity_verified: false`, `timing_verified: false`,
`automated_patch_selection: false` and `needs_review: true`. Collection does not
automatically import an experimental bounce into the production library/cache.

## What would justify an automated backend

1. Inspect a real Logic 12.0 installation's dictionary and UI. Record the Logic
   version, UI language and any changed key commands. Prefer direct scripting
   if it genuinely supports the required operations.
2. Demonstrate one native synth factory patch in the dedicated project. Compare
   the bounce by listening and inspect the displayed patch/instrument identity.
3. Repeat with a sample-based patch and a stack/send patch. Check sound downloads,
   effect tails, output routing and offline-vs-real-time behavior. A selected
   track's patch must not be inferred solely from its editable track name.
4. Only then add UI operations with bounded waits, explicit ownership of the
   test project and stop-on-unrecognized-dialog behavior. Observe patch selection
   and loading completion; do not drive whatever project happens to be active.
5. Add a small repeated batch with per-patch records and explicit manual-review
   status until stronger preset-load verification is available. Test cancellation
   without closing the user's other projects or terminating unrelated processes.

## Validation and limits

The cloud workspace is Linux and has no licensed Logic installation or graphical
Logic session. This branch therefore **does not claim an actual Logic render or
a successful Logic 12.0 UI probe**. Unit tests exercise standard MIDI event timing,
isolated template copying, input preservation, catalog traversal, read-only probe
contracts, synthetic 16/24-bit WAV checks and atomic collection races. JavaScript
syntax checking does not validate JXA's live System Events object model.

The existing desktop/AU renderers are unchanged. Existing documentation is
unchanged; this investigation is a new document.

## Sources reviewed

- [Apple: Choose patches in the Library](https://support.apple.com/guide/logicpro/choose-a-patch-lgcp689ae6e8/mac)
- [Apple: Patches overview](https://support.apple.com/guide/logicpro/patches-overview-lgcp2bc4b02f/mac)
- [Apple: Bounce a project to an audio file](https://support.apple.com/guide/logicpro/bounce-a-project-to-an-audio-file-lgcp785a41c3/mac)
- [Apple: Bounce in place overview](https://support.apple.com/guide/logicpro/bounce-in-place-overview-lgcpe2a9f868/mac)
- [Apple: Automating the User Interface](https://developer.apple.com/library/archive/documentation/LanguagesUtilities/Conceptual/MacAutomationScriptingGuide/AutomatetheUserInterface.html)

The UI-scripting source is archived Apple documentation; its older System
Preferences navigation is not the current macOS Settings layout.
