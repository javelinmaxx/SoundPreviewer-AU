#!/usr/bin/env python3
"""Experimental Logic Pro 12 factory-patch rendering investigation.

Probe reads automation capabilities. Prepare creates MIDI and an isolated test
workspace. Collect validates a manually bounced WAV; it does not claim to have
selected or verified the instrument inside Logic.
"""
from __future__ import annotations

import argparse
import array
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import uuid
import wave
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parent.parent
MAX_BYTES = 256 * 1024 * 1024
PPQ = 960
TARGET = "12.0"


def digest(path: Path) -> str:
    result = hashlib.sha256()
    total = 0
    with path.open("rb") as source:
        while chunk := source.read(65536):
            total += len(chunk)
            if total > MAX_BYTES:
                raise ValueError("File exceeds the 256 MiB experiment limit.")
            result.update(chunk)
    return result.hexdigest()


def write_json(path: Path, value):
    with path.open("x", encoding="utf-8") as target:
        json.dump(value, target, indent=2, ensure_ascii=False, allow_nan=False)
        target.write("\n")


def tree(path: Path):
    """Bound template copying and refuse links rather than following sample libraries."""
    pending, members, total = [path], [], 0
    while pending:
        member = pending.pop()
        if member.is_symlink():
            raise ValueError(f"Linked template members are not supported: {member}")
        if len(members) >= 4096:
            raise ValueError("Use a small dedicated template (at most 4096 members).")
        members.append(member)
        if member.is_dir():
            pending.extend(member.iterdir())
        elif member.is_file():
            total += member.stat().st_size
            if total > MAX_BYTES:
                raise ValueError("Use a small dedicated template (at most 256 MiB).")
        else:
            raise ValueError(f"Template contains a non-file member: {member}")
    return members


def inventory(roots):
    patches, locations = [], []
    for root in roots:
        if root.is_symlink():
            raise ValueError(f"Select an unlinked patch root: {root}")
        locations.append({"path": str(root), "available": root.is_dir()})
        if not root.is_dir():
            continue
        def fail(error):
            raise error
        for parent, directories, _ in os.walk(root, topdown=True, followlinks=False, onerror=fail):
            if Path(parent).suffix.lower() == ".patch":
                patches.append({"name": Path(parent).stem, "path": str(Path(parent).absolute())})
                directories[:] = []
            else:
                directories[:] = [name for name in directories if not (Path(parent) / name).is_symlink()]
            if len(patches) > 50000:
                raise ValueError("More than 50,000 patches; choose a smaller root.")
    return {"roots": locations, "patches": sorted({item["path"]: item for item in patches}.values(), key=lambda item: item["path"]),
            "complete_factory_catalog_verified": False, "installed_sound_assets_verified": False}


def copy_template_member(source: Path, destination: Path):
    before = source.stat()
    total = 0
    with source.open("rb") as original, destination.open("xb") as copied:
        while chunk := original.read(65536):
            total += len(chunk)
            if total > before.st_size or total > MAX_BYTES:
                raise ValueError("Template changed or exceeded its copy limit.")
            copied.write(chunk)
    after = source.stat()
    if total != before.st_size or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("Template changed while copying. Save it first, then retry.")


def variable(value: int) -> bytes:
    if not 0 <= value <= 0x0FFFFFFF:
        raise ValueError("MIDI delta exceeds the SMF limit.")
    result = [value & 127]
    while value := value >> 7:
        result.insert(0, (value & 127) | 128)
    return bytes(result)


def make_midi(notes=(60, 84, 36), velocity=89, bpm=120, hold=0.5, tail=1.0, lead_beats=4):
    if (len(notes) != 3 or any(type(note) is not int or not 0 <= note <= 127 for note in notes)
            or type(velocity) is not int or not 1 <= velocity <= 127
            or type(bpm) is not int or not 20 <= bpm <= 300
            or not all(math.isfinite(value) and 0 <= value <= 10 for value in (hold, tail)) or hold < 0.05):
        raise ValueError("Choose three MIDI notes, velocity 1–127, BPM 20–300, hold 0.05–10s, tail 0–10s.")
    tempo = round(60_000_000 / bpm)
    seconds_per_tick = tempo / 1_000_000 / PPQ
    hold_ticks = round(hold / seconds_per_tick)
    tail_ticks = round(tail / seconds_per_tick)
    start = lead_beats * PPQ
    track = b"\0\xff\x51\x03" + tempo.to_bytes(3, "big")
    track += b"\0\xff\x58\x04\x04\x02\x18\x08"  # 4/4
    label = b"SoundPreviewer Logic rendering experiment"
    track += b"\0\xff\x03" + variable(len(label)) + label
    spans = []
    for index, pitch in enumerate(notes):
        track += variable(start if index == 0 else tail_ticks) + bytes((0x90, pitch, velocity))
        track += variable(hold_ticks) + bytes((0x80, pitch, 0))
        spans.append({"pitch": pitch, "start_seconds": start * seconds_per_tick,
                      "hold_seconds": hold_ticks * seconds_per_tick,
                      "tail_seconds": tail_ticks * seconds_per_tick})
        start += hold_ticks + tail_ticks
    track += variable(tail_ticks) + b"\xff\x2f\0"
    midi = b"MThd" + struct.pack(">IHHH", 6, 0, 1, PPQ) + b"MTrk" + struct.pack(">I", len(track)) + track
    return midi, {"notes": spans, "velocity": velocity, "bpm": bpm, "tempo_us": tempo,
                  "lead_in_seconds": lead_beats * PPQ * seconds_per_tick,
                  "expected_seconds": start * seconds_per_tick}


def prepare(workspace: Path, name: str, template: Path | None = None, **midi_options):
    if not name.strip() or len(name) > 200:
        raise ValueError("Supply the exact factory Library patch name (1–200 characters).")
    workspace = workspace.absolute()
    if workspace.exists() or workspace.is_symlink():
        raise ValueError("Workspace already exists; choose a new directory. Nothing was replaced.")
    members = None
    if template is not None:
        template = template.absolute()
        if template.suffix.lower() != ".logicx" or not template.exists():
            raise ValueError("Template must be an existing dedicated .logicx project.")
        if workspace.resolve().is_relative_to(template.resolve()):
            raise ValueError("Workspace must be outside the source template.")
        members = tree(template)
    midi, timing = make_midi(**midi_options)
    workspace.mkdir(parents=True, exist_ok=False)
    try:
        if members is not None:
            for member in members:
                destination = workspace / "Session.logicx" / member.relative_to(template) if template.is_dir() else workspace / "Session.logicx"
                if member.is_dir():
                    destination.mkdir(parents=True, exist_ok=True)
                else:
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    copy_template_member(member, destination)
        (workspace / "Preview.mid").write_bytes(midi)
        request = {"schema_version": 1, "experiment_id": uuid.uuid4().hex, "target_logic_version": TARGET,
                   "patch_name": name, "patch_origin": "factory Library (user-selected)",
                   "status": "awaiting_manual_logic_bounce", "automated_patch_selection": False,
                   "sample_rate": 44100, "channels": 2, "timing": timing,
                   "midi_sha256": digest(workspace / "Preview.mid"), "template_copied": members is not None,
                   "source_template": str(template) if template else None}
        write_json(workspace / "request.json", request)
        instructions = f"""Logic Pro {TARGET} factory patch experiment: {name}

1. Open Session.logicx here, or create a NEW dedicated Logic project if no template was supplied.
2. Use the stereo output with no extra mastering effects or automation. Set tempo to {timing['bpm']} BPM.
3. Create/select a software instrument track and choose the exact factory Library patch: {name}
   Keep its complete stack, buses and effects. Wait for all required sounds to finish loading.
4. Import Preview.mid at bar 1 onto that instrument track, preserving its embedded note positions.
   If Logic opens MIDI as a new project, move its region into this dedicated project instead.
   The first note starts after {timing['lead_in_seconds']:.6f}s (one bar); do not shift away this lead-in.
5. Bounce the stereo PROJECT output from bar 1, covering at least {timing['expected_seconds']:.6f}s.
   Choose PCM WAV, 44.1 kHz, 16-bit or 24-bit integer, normalization OFF, no MP3/AAC/dither.
   Include the audio tail. Save it here as Bounce.wav. Extra tail allowance is 10s.
6. Run collect with this workspace and --confirmed-patch matching the exact Library name.

This is a manual experiment. No script has selected the patch or verified its identity.
Do not use an unrelated or unsaved working project. The original template is never opened or edited.
"""
        (workspace / "STEPS.txt").write_text(instructions, encoding="utf-8")
        return request
    except BaseException:
        # This directory was created exclusively by this invocation; no old data is removed.
        shutil.rmtree(workspace)
        raise


def validate_bounce(path: Path, request: dict):
    if path.is_symlink() or not path.is_file() or not 44 <= path.stat().st_size <= MAX_BYTES:
        raise ValueError("Bounce must be a regular WAV no larger than 256 MiB.")
    with wave.open(str(path), "rb") as audio:
        rate, channels, width, frames = audio.getframerate(), audio.getnchannels(), audio.getsampwidth(), audio.getnframes()
        if audio.getcomptype() != "NONE" or width not in (2, 3, 4):
            raise ValueError("Export uncompressed integer PCM WAV (16/24/32 bit).")
        if rate != request["sample_rate"] or channels != request["channels"]:
            raise ValueError("Bounce sample rate/channels differ from the experiment (44.1 kHz stereo).")
        duration = frames / rate
        if not request["timing"]["expected_seconds"] - 1 / rate <= duration <= request["timing"]["expected_seconds"] + 10:
            raise ValueError("Bounce duration is outside the experiment range. Bounce from bar 1 including the MIDI lead-in.")
        ranges = [(round(note["start_seconds"] * rate), round((note["start_seconds"] + note["hold_seconds"]) * rate))
                  for note in request["timing"]["notes"]]
        energy, counts, peak, position = [0.0] * 3, [0] * 3, 0.0, 0
        while data := audio.readframes(4096):
            if len(data) % (width * channels):
                raise ValueError("Bounce contains a partial PCM frame.")
            if width in (2, 4):
                values = array.array("h" if width == 2 else "i", data)
                if sys.byteorder != "little":
                    values.byteswap()
            else:
                values = [int.from_bytes(data[i:i + 3], "little", signed=True) for i in range(0, len(data), 3)]
            for index, value in enumerate(values):
                normalized = value / (1 << (8 * width - 1))
                peak = max(peak, abs(normalized))
                frame = position + index // channels
                for slot, (start, end) in enumerate(ranges):
                    if start <= frame < end:
                        energy[slot] += normalized * normalized
                        counts[slot] += 1
            position += len(data) // (width * channels)
        if position != frames:
            raise ValueError("Bounce data is truncated.")
        rms = [math.sqrt(value / count) if count else 0 for value, count in zip(energy, counts)]
        if min(rms) < 0.00001:
            raise ValueError("At least one note window is silent. Check MIDI placement, patch range and sound downloads.")
        return {"frames": frames, "sample_rate": rate, "channels": channels, "bits": width * 8,
                "duration_seconds": duration, "note_rms": rms, "peak": peak, "clipping_detected": peak >= 0.999}


def collect(workspace: Path, bounce: Path, confirmed_patch: str):
    request_path = workspace / "request.json"
    if request_path.stat().st_size > 1024 * 1024:
        raise ValueError("Experiment request exceeds 1 MiB.")
    request = json.loads(request_path.read_text(encoding="utf-8"))
    if request.get("schema_version") != 1 or request.get("status") != "awaiting_manual_logic_bounce":
        raise ValueError("Not a supported Logic rendering experiment.")
    if confirmed_patch != request["patch_name"]:
        raise ValueError("Confirm the exact factory Library patch name used for this bounce.")
    if digest(workspace / "Preview.mid") != request["midi_sha256"]:
        raise ValueError("The experiment's MIDI file changed. Prepare a new experiment.")
    if (workspace / "preview.wav").exists() or (workspace / "result.json").exists():
        raise ValueError("Experiment already has collected output. Prepare a new workspace; nothing was replaced.")
    if bounce.is_symlink() or not bounce.is_file() or not 44 <= bounce.stat().st_size <= MAX_BYTES:
        raise ValueError("Bounce must be a regular WAV no larger than 256 MiB.")
    before = digest(bounce)
    metrics = validate_bounce(bounce, request)
    if digest(bounce) != before:
        raise ValueError("Bounce changed while validating. Wait until Logic finishes writing.")
    # Stage the exact checked bytes and publish without overwriting any existing audio.
    with tempfile.TemporaryDirectory(prefix=".collect-", dir=workspace) as temporary:
        staged = Path(temporary) / "preview.wav"
        shutil.copyfile(bounce, staged)
        if digest(staged) != before:
            raise ValueError("Bounce changed while copying. Nothing was published.")
        result = {"schema_version": 1, "experiment_id": request["experiment_id"],
                  "patch_name": confirmed_patch, "target_logic_version": request["target_logic_version"],
                  "method": "manual Logic project bounce", "automated_patch_selection": False,
                  "preset_identity_verified": False, "identity_evidence": "user confirmation only",
                  "timing_verified": False, "note_window_energy_checked": True,
                  "output": str((workspace / "preview.wav").absolute()), "wav_sha256": before,
                  "metrics": metrics, "needs_review": True}
        write_json(Path(temporary) / "result.json", result)
        published = False
        try:
            os.link(staged, workspace / "preview.wav")
            published = True
            os.link(Path(temporary) / "result.json", workspace / "result.json")
        except BaseException:
            if published:
                (workspace / "preview.wav").unlink()
            raise
    return result


def probe(application: Path, accessibility=False):
    if sys.platform != "darwin":
        raise ValueError("Logic capability probing needs your Mac. Prepare/MIDI tests can run on other systems.")
    with (application / "Contents/Info.plist").open("rb") as source:
        info = plistlib.load(source)
    if info.get("CFBundleIdentifier") != "com.apple.logic10":
        raise ValueError("Selected application is not Logic Pro.")
    version = info.get("CFBundleShortVersionString", "unknown")
    report = {"application": str(application), "version": version,
              "target_version_match": version == TARGET or version.startswith(TARGET + "."),
              "target_version": TARGET, "bundle_id": info["CFBundleIdentifier"], "logic_launched": False}
    # sdef extracts the installed app's public dictionary, if it has one. Do not
    # infer headless rendering support from generic open/close Apple Events.
    definition = subprocess.run(["/usr/bin/sdef", str(application)], capture_output=True, timeout=30)
    report["sdef_exit_code"] = definition.returncode
    if definition.returncode == 0 and len(definition.stdout) <= 4 * 1024 * 1024:
        commands = [node.attrib for node in ET.fromstring(definition.stdout).iter("command")]
        report["scripting_commands"] = commands
        report["render_related_command_candidates"] = [item for item in commands if any(
            word in item.get("name", "").lower() for word in ("bounce", "render", "patch"))]
    else:
        report["sdef_diagnostic"] = definition.stderr.decode(errors="replace")[-3000:]
    if accessibility:
        script = Path(__file__).with_name("logic_render_probe.js")
        outcome = subprocess.run(["/usr/bin/osascript", "-l", "JavaScript", str(script)],
                                 capture_output=True, text=True, timeout=30)
        report["accessibility_exit_code"] = outcome.returncode
        report["accessibility"] = json.loads(outcome.stdout) if outcome.returncode == 0 else {"error": outcome.stderr[-3000:]}
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("probe", help="Read Logic version/public dictionary; optional read-only UI inventory")
    p.add_argument("--application", type=Path, default=Path("/Applications/Logic Pro.app"))
    p.add_argument("--accessibility", action="store_true", help="Read running Logic's UI; macOS may request Automation/Accessibility access")
    p = sub.add_parser("inventory", help="List .patch packages without loading them; the factory catalog may use other locations")
    p.add_argument("--root", type=Path, action="append", help="Repeat for multiple installed patch folders")
    p = sub.add_parser("prepare", help="Create MIDI and instructions for one manual factory-patch bounce")
    p.add_argument("--name", required=True, help="Exact factory Library patch name")
    p.add_argument("--workspace", type=Path, default=PROJECT / "artifacts/logic-render-lab" / (datetime.datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8]))
    p.add_argument("--template", type=Path, help="Optional dedicated empty .logicx project to copy (never opened/edited)")
    p.add_argument("--notes", default="60,84,36")
    p.add_argument("--velocity", type=int, default=89)
    p.add_argument("--bpm", type=int, default=120)
    p.add_argument("--hold", type=float, default=0.5)
    p.add_argument("--tail", type=float, default=1.0)
    p = sub.add_parser("collect", help="Validate and preserve a manually bounced PCM WAV")
    p.add_argument("--workspace", type=Path, required=True)
    p.add_argument("--bounce", type=Path, required=True)
    p.add_argument("--confirmed-patch", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "probe":
            result = probe(args.application, args.accessibility)
        elif args.command == "inventory":
            result = inventory(args.root or [Path("/Applications/Logic Pro.app/Contents/Resources/Patches"),
                                            Path("/Library/Application Support/Logic/Patches")])
        elif args.command == "prepare":
            result = prepare(args.workspace, args.name, args.template, notes=tuple(int(value) for value in args.notes.split(",")),
                             velocity=args.velocity, bpm=args.bpm, hold=args.hold, tail=args.tail)
            result["workspace"] = str(args.workspace.absolute())
        else:
            result = collect(args.workspace, args.bounce, args.confirmed_patch)
        print(json.dumps(result, indent=2, ensure_ascii=False, allow_nan=False))
        return 0
    except (OSError, ValueError, KeyError, TypeError, wave.Error, ET.ParseError, subprocess.SubprocessError) as error:
        print("Logic render lab: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
