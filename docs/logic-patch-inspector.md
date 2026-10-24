# Logic patch inspection

The first stage of Logic `.patch` support is a **read-only command-line inspector**.
It is separate from library scanning and preview generation; it does not add a
Logic rendering backend or change the application UI. Requires Python 3.10+ and
only the standard library. Existing renderer environments can run it too.

From the repository root:

```bash
python3 scripts/logic_patch_inspector.py "/path/to/My Sound.patch"
python3 scripts/logic_patch_inspector.py "/path/to/My Sound.patch" --json
```

On your Mac, optionally compare saved AU identities with registered components:

```bash
python3 scripts/logic_patch_inspector.py "/path/to/My Sound.patch" --check-installed
```

This invokes `/usr/bin/auval -a` to enumerate registrations. It does not run AU
validation, instantiate an instrument, play notes, or render audio. An AU being
registered does not establish that it can load, is licensed, supports the saved
state, has compatible architecture, or can find its samples.

A listing captured on a Mac can also be inspected on another machine:

```bash
auval -a > "$HOME/Desktop/au-inventory.txt"
python3 scripts/logic_patch_inspector.py "/path/to/My Sound.patch" \
  --au-inventory "$HOME/Desktop/au-inventory.txt" --json
```

Without a listing, availability is `not_checked`, never assumed missing.
`registered` and `not_in_inventory` compare the exact type/subtype/manufacturer
FourCC triple, including spaces. Conflicting wrapper/state identities are marked
`identity_conflict`. Logic-native PST states are marked
`logic_native_hostability_unknown`; native plug-in IDs aren't treated as AU IDs.
The inspector fails an unreadable inventory rather than treating it as an empty
set of installed components.

## Report contents

- Package metadata from XML or binary `data.plist`, including unknown fields.
- Every directly contained `.cst`, including strips absent from the channel list.
- Instruments, MIDI FX and audio FX in container order, with slot numbers.
- Names from the wrapper and saved preset, where present; display names may be
  truncated in the binary wrapper and are not used as dependency identities.
- Saved input/output routing, send dictionaries and other channel settings.
- Hashes and sizes of inspected files, saved state and opaque blocks. Plug-in
  parameter blobs are not dumped or decoded into speculative parameter names.
- Explicit warnings about opaque Logic metadata, unrecognized blocks and tails,
  unreadable channel strips, populated sends and unverified format versions.

`inspected_state_sha256` covers `data.plist` and all inspected CST filenames and
bytes. It is independent of the package's installation path. It excludes external
samples, other package members, installed plug-ins and runtime versions, so it is
**not a complete rendering cache key**. Files may change while being inspected;
a future rendering worker must stage and verify a consistent input snapshot.

`rendering_supported` is always `false`. Exit status 0 means a report was produced,
including reports with opaque state or unreadable channel warnings; it does not
mean the patch is renderable. Exit status 1 means inspection or inventory access
failed; malformed command-line arguments use argparse's exit status 2.

## Scope and boundaries

Observed patches contain `#Root.cst`, optional additional CSTs, and `data.plist`.
CST is an OCuA binary container whose UCuA blocks may embed Logic PST state, XML or
binary AU plists, or internal NSKeyedArchiver metadata. The inspector reads these
envelopes without deserializing Objective-C classes or interpreting plug-in code.

Source files are opened for reading only. Linked package roots/inspected members, unsafe
member names and special files are rejected. Inspection is bounded to 128 strips,
64 MiB per member, 256 MiB per package, 8 MiB per plist, 1,024 directory entries
and 4,096 blocks per CST.
Channel filenames currently must refer to direct package members. Other package
members are listed as uninspected. Structural errors are not converted into
successful plug-in matches.

The format is reverse-engineered, not a published Apple specification. In
particular, populated send dictionaries, keyboard zones, Smart Controls, bypass
state and other opaque metadata are not reconstructed. No plug-in or external
asset dependency is inferred from an opaque block. A native Logic instrument's
saved state does not supply its sound engine or prove that an external host can
load it. Effects-only audio patches also need source audio rather than preview
MIDI to make a useful sound.

The intended next stage is a separate Mac AU rendering worker for chains whose
components, state and routing can be validated. The current VST renderer remains
separate. Unsupported graph elements must be reported before attempting rendering.

## Validation and format reference

```bash
python3 -m unittest discover -s tests/scripts -p 'test_*.py'
```

Tests use synthetic fixtures, never commercial plug-ins. The parser was also
checked read-only against the four `.patch` examples in
[LogicFiles commit 3234062](https://github.com/CraigStuntz/LogicFiles/tree/3234062e16f8b5dc10d9ddcc68454013d97c44c0/Tests/Resources/examples),
including a multi-instrument summing stack. That corpus's `VersionPatches` value
is 40014; other versions remain explicitly unverified. Native Mac inventory
enumeration is not validated by Linux tests.

Container layout and role bits are informed by the MIT-licensed
[LogicFiles format research](https://github.com/CraigStuntz/LogicFiles/tree/3234062e16f8b5dc10d9ddcc68454013d97c44c0/Sources/Models).
Its license is retained in [third-party/logicfiles/LICENSE](../third-party/logicfiles/LICENSE).
