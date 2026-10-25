#!/usr/bin/env python3
"""Render a bounded, single-instrument Logic AU chain using the native Mac worker."""
from __future__ import annotations

import argparse
import array
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import wave

import logic_patch_inspector as inspector

PROJECT = Path(__file__).resolve().parent.parent
DEFAULT_WORKER = PROJECT / ".build/logic-preview/LogicPreview"


class PreviewError(ValueError):
    """A patch cannot be represented or a worker failed to produce valid audio."""


def validate_options(notes, velocity, sample_rate, hold, tail, bpm, timeout):
    if not (1 <= len(notes) <= 16 and all(type(note) is int and 0 <= note <= 127 for note in notes)
            and 1 <= velocity <= 127 and sample_rate in (44100, 48000)
            and math.isfinite(hold) and 0.05 <= hold <= 5 and math.isfinite(tail) and 0 <= tail <= 5
            and (hold + tail) * len(notes) <= 60 and math.isfinite(bpm) and 20 <= bpm <= 300
            and math.isfinite(timeout) and 1 <= timeout <= 600):
        raise PreviewError("Invalid preview timing, notes, velocity, sample rate, tempo or timeout.")


def plan_patch(report: dict, au_chain_only=False) -> dict:
    if report["patch_format_version"] != 40014 or len(report["channels"]) != 1:
        raise PreviewError("Only observed format 40014 patches with one channel strip are supported; track stacks require a routing backend.")
    channel = report["channels"][0]
    settings = channel["settings"] or {}
    if channel["filename"] != "#Root.cst" or channel.get("channel_type") != "instrument" or settings.get("Root") is not True:
        raise PreviewError("Preview requires a single root instrument strip. Audio-only patches need source audio.")
    if settings.get("Channel_outputIsBus") is not False or settings.get("Channel_outputIndex") != 0:
        raise PreviewError("Bus/external or unknown output routing is not supported.")
    if settings.get("Channel_inputIsBus", False) or any(settings.get("Channel_sends", [])):
        raise PreviewError("Bus inputs and populated sends cannot be reconstructed by this preview host.")
    if settings.get("Channel_isMuted", False):
        raise PreviewError("The saved strip is muted; refusing to publish a misleading unmuted preview.")
    if any("Unreadable" in message or "container tail" in message for message in channel["warnings"]):
        raise PreviewError("Channel-strip structure is incomplete or unrecognized.")
    extra = set(report["uninspected_members"]) - {".DS_Store"}
    if extra:
        raise PreviewError("Additional package members may carry dependencies: " + ", ".join(sorted(extra)))
    plugins = channel["plugins"]
    if not plugins or len({p["slot"] for p in plugins}) != len(plugins):
        raise PreviewError("The strip has no recognized instrument or contains ambiguous duplicate slots.")
    for plugin in plugins:
        if plugin["state_format"] != "au_preset":
            raise PreviewError(f"Slot {plugin['slot']}: Logic-native PST state needs Logic itself; an external AU worker cannot restore it.")
        component = plugin["component"]
        if component is None or plugin.get("identity_conflict"):
            raise PreviewError(f"Slot {plugin['slot']}: missing/conflicting AU component identity.")
        expected = {"instrument": "aumu", "audio_fx": "aufx"}.get(plugin["role"])
        if expected is None or component["type"] != expected:
            raise PreviewError(f"Slot {plugin['slot']}: MIDI effects, generators and ambiguous roles are not supported.")
        if plugin["role_source"] == "wrapper_bits" and plugin.get("wrapper_component") != component:
            raise PreviewError(f"Slot {plugin['slot']}: the wrapper does not confirm the saved-state AU identity.")
    instruments = [p for p in plugins if p["role"] == "instrument"]
    effects = [p for p in plugins if p["role"] == "audio_fx"]
    if len(instruments) != 1 or len(effects) > 8:
        raise PreviewError("Exactly one AU instrument and at most eight audio effects are supported.")
    descriptive = {"Filename", "Root", "UUID", "Channel_name", "Channel_instID", "Channel_seqColorIndex",
                   "Track_icon", "Channel_chaStrCategory", "Channel_inputIndex_1", "Channel_inputIsBus",
                   "Channel_outputIndex", "Channel_outputIsBus", "Channel_outputIsStereo", "Channel_sends",
                   "Channel_isMuted", "Channel_isSolo", "Channel_receiveChannel", "Channel_userDidModifySmartControls"}
    approximations = []
    unknown_patch_keys = set(report["metadata"]) - {"VersionPatches", "channels"}
    if unknown_patch_keys:
        approximations.append("Uninterpreted patch settings are omitted: " + ", ".join(sorted(unknown_patch_keys)))
    if channel["opaque_blocks"]:
        approximations.append("Opaque Logic blocks are omitted, including any keyboard zones, bypass, Smart Controls or unrecognized state they contain.")
    unknown_keys = set(settings) - descriptive
    if unknown_keys:
        approximations.append("Uninterpreted channel controls are omitted: " + ", ".join(sorted(unknown_keys)))
    if settings.get("Channel_inputIndex_1", -1) != -1 or settings.get("Channel_receiveChannel", 0) not in (0, 1):
        approximations.append("Saved input/MIDI channel selection is replaced with preview MIDI on channel 1.")
    if settings.get("Channel_outputIsStereo") is not True:
        approximations.append("Saved output format is not confirmed stereo; preview uses stereo.")
    if approximations and not au_chain_only:
        raise PreviewError("The patch contains uninterpreted Logic state. Use --au-chain-only explicitly for an approximate recognized-AU-chain preview. " + " ".join(approximations))
    return {"instrument": instruments[0], "effects": effects, "approximations": approximations,
            "scope": "recognized_au_chain_only", "logic_equivalence_verified": False}


def _extract_states(data: bytes) -> dict[int, bytes]:
    states = {}
    position = inspector._u32(data, 28) + 36
    while data[position:position + 4] == b"UCuA":
        size = inspector._u32(data, position + 28) + 36
        block = data[position:position + size]
        slot = int.from_bytes(block[18:20], "little")
        subheader = inspector._u32(block, 36)
        payload = inspector._payload(block, subheader) if slot < 12 and subheader in (64, 424) else None
        if payload and payload[0] == "au_preset":
            if slot in states:
                raise PreviewError("Duplicate AU state slot.")
            states[slot] = payload[2]
        position += size
    return states


def _run_worker(worker: Path, manifest: Path, timeout: float, cancel_event=None, inherit_process_group=False):
    process = subprocess.Popen([str(worker), "--request", str(manifest)], stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, start_new_session=not inherit_process_group)
    try:
        if cancel_event is None:
            output, errors = process.communicate(timeout=timeout)
        else:
            deadline = time.monotonic() + timeout
            while True:
                if cancel_event.is_set():
                    raise KeyboardInterrupt()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(str(worker), timeout)
                try:
                    output, errors = process.communicate(timeout=min(0.25, remaining))
                    break
                except subprocess.TimeoutExpired:
                    continue
    except (subprocess.TimeoutExpired, KeyboardInterrupt):
        # Only terminate the group created for this render, never another app's AU host.
        try:
            if inherit_process_group:
                process.terminate()
            else:
                os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.communicate(timeout=2)
        except subprocess.TimeoutExpired:
            try:
                if inherit_process_group:
                    process.kill()
                else:
                    os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.communicate()
        raise
    if process.returncode:
        raise PreviewError(f"AU worker failed ({process.returncode}): {(errors or output)[-4000:].strip()}")


def validate_wav(path: Path, request: dict) -> dict:
    segment = math.floor(request["holdSeconds"] * request["sampleRate"] + 0.5) + math.floor(request["tailSeconds"] * request["sampleRate"] + 0.5)
    expected_frames = segment * len(request["notes"])
    if path.is_symlink() or not path.is_file() or path.stat().st_size != 44 + expected_frames * 4:
        raise PreviewError("Worker output is missing, linked, or has an unexpected WAV size.")
    energies = [0.0] * len(request["notes"])
    peak = 0
    with wave.open(str(path), "rb") as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getnframes()) != (2, 2, request["sampleRate"], expected_frames):
            raise PreviewError("Worker output does not match the requested stereo PCM16 format/timing.")
        for region in range(len(energies)):
            remaining = segment
            while remaining:
                frames = min(4096, remaining)
                raw = source.readframes(frames)
                if len(raw) != frames * 4:
                    raise PreviewError("Worker output has truncated PCM data.")
                samples = array.array("h", raw)
                if sys.byteorder != "little":
                    samples.byteswap()
                peak = max(peak, max(abs(value) for value in samples))
                energies[region] += sum((value / 32768) ** 2 for value in samples)
                remaining -= frames
    rms = [math.sqrt(value / (segment * 2)) for value in energies]
    if any(value <= 1e-7 for value in rms):
        raise PreviewError("At least one preview note is silent; the preset/range/samples must be checked.")
    return {"frames": expected_frames, "sample_rate": request["sampleRate"], "channels": 2,
            "peak": peak / 32768, "note_rms": rms}


def render_preview(patch: Path, output: Path, worker=DEFAULT_WORKER, *, au_chain_only=False,
                   notes=(60, 84, 36), velocity=89, sample_rate=44100, hold=0.5, tail=0.35, bpm=120, timeout=120,
                   cancel_event=None, inherit_process_group=False) -> dict:
    if sys.platform != "darwin":
        raise PreviewError("Native AU preview requires macOS. Inspection and planning can run on other systems.")
    patch = inspector.resolve_patch_path(patch)
    validate_options(notes, velocity, sample_rate, hold, tail, bpm, timeout)
    source_root = patch.resolve()
    destination = output.absolute()
    if destination.suffix.lower() != ".wav" or destination.resolve().is_relative_to(source_root):
        raise PreviewError("Output must be a .wav outside the source patch package.")
    if destination.exists() or destination.is_symlink():
        raise PreviewError("Output already exists; choose a new path to preserve previous audio.")
    worker = Path(worker).absolute()
    if not worker.is_file() or not os.access(worker, os.X_OK):
        raise PreviewError("Native worker is missing. Run bash scripts/Setup-LogicPreview.sh first.")
    report = inspector.inspect_patch(patch)
    plan = plan_patch(report, au_chain_only)
    with tempfile.TemporaryDirectory(prefix="soundpreviewer-logic-") as temporary:
        staging = Path(temporary)
        snapshot = staging / "source.patch"
        snapshot.mkdir()
        for name in ["data.plist"] + [channel["filename"] for channel in report["channels"]]:
            (snapshot / name).write_bytes(inspector._read_member(source_root, name, inspector.MAX_PACKAGE_BYTES))
        staged_report = inspector.inspect_patch(snapshot)
        if staged_report["inspected_state_sha256"] != report["inspected_state_sha256"]:
            raise PreviewError("Patch changed while staging; retry after saving has finished.")
        states = _extract_states((snapshot / "#Root.cst").read_bytes())
        plugins = []
        for index, plugin in enumerate([plan["instrument"]] + plan["effects"]):
            raw = states.get(plugin["slot"])
            if raw is None or hashlib.sha256(raw).hexdigest() != plugin["saved_state"]["sha256"]:
                raise PreviewError("Staged AU state does not match the inspected slot.")
            name = f"state-{index}.aupreset"
            (staging / name).write_bytes(raw)
            plugins.append({"component": plugin["component"], "stateFile": name, "stateSHA256": hashlib.sha256(raw).hexdigest()})
        request = {"schemaVersion": 1, "instrument": plugins[0], "effects": plugins[1:], "notes": list(notes),
                   "velocity": velocity, "sampleRate": sample_rate, "holdSeconds": hold,
                   "tailSeconds": tail, "bpm": bpm}
        manifest = staging / "manifest.json"
        manifest.write_text(json.dumps(request, allow_nan=False), encoding="utf-8")
        _run_worker(worker, manifest, timeout, cancel_event, inherit_process_group)
        result_path = staging / "result.json"
        if result_path.is_symlink() or not result_path.is_file() or result_path.stat().st_size > 1024 * 1024:
            raise PreviewError("Worker returned no valid bounded result file.")
        result = json.loads(result_path.read_text(encoding="utf-8"))
        if not isinstance(result, dict):
            raise PreviewError("Worker result must be a JSON object.")
        audio = staging / "preview.wav"
        metrics = validate_wav(audio, request)
        if (result.get("success") is not True or result.get("schemaVersion") != 1
                or result.get("frames") != metrics["frames"] or result.get("sampleRate") != sample_rate
                or result.get("channels") != 2):
            raise PreviewError("Worker result and validated audio disagree.")
        segment = metrics["frames"] // len(notes)
        results = result.get("notes")
        if not isinstance(results, list) or len(results) != len(notes) or any(
                not isinstance(note, dict) or note.get("pitch") != pitch
                or note.get("startFrame") != index * segment or note.get("frames") != segment
                for index, (note, pitch) in enumerate(zip(results, notes))):
            raise PreviewError("Worker note provenance/timing does not match the requested sequence.")
        destination.parent.mkdir(parents=True, exist_ok=True)
        # Publish without replacing an old preview, even if another process wins
        # the filename race. The temporary file lives on the destination volume.
        with tempfile.NamedTemporaryFile(prefix=".logic-preview-", dir=destination.parent, delete=False) as target:
            publish = Path(target.name)
            try:
                with audio.open("rb") as source:
                    shutil.copyfileobj(source, target)
                target.flush()
                os.fsync(target.fileno())
            except BaseException:
                publish.unlink(missing_ok=True)
                raise
        try:
            os.link(publish, destination)
        finally:
            publish.unlink(missing_ok=True)
    return {"success": True, "output": str(destination), "scope": plan["scope"],
            "logic_equivalence_verified": False, "approximations": plan["approximations"],
            "inspected_state_sha256": report["inspected_state_sha256"], "metrics": metrics,
            "notes": list(notes), "component_chain": [plugin["component"] for plugin in plugins]}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("patch", type=Path, help="Logic .patch package or library folder (folders render recursively)")
    parser.add_argument("--output", type=Path, help="New WAV filename; defaults to a unique artifacts/logic-preview file")
    parser.add_argument("--recursive", "-r", action="store_true", help="Batch-render patches recursively, also accepting a single patch")
    parser.add_argument("--output-dir", type=Path, help="Batch output folder; mirrors source subfolders and preserves existing WAVs")
    parser.add_argument("--worker", type=Path, default=DEFAULT_WORKER)
    parser.add_argument("--au-chain-only", action="store_true", help="Explicitly preview the recognized AU chain while omitting uninterpreted Logic metadata/mixer controls")
    parser.add_argument("--play", action="store_true", help="Play the validated WAV with macOS afplay")
    parser.add_argument("--no-play", dest="play", action="store_false", help="Render without playback (overrides the launcher's default)")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--notes", default="60,84,36")
    parser.add_argument("--hold", type=float, default=0.5)
    parser.add_argument("--tail", type=float, default=0.35)
    parser.add_argument("--velocity", type=int, default=89)
    parser.add_argument("--sample-rate", type=int, default=44100)
    parser.add_argument("--bpm", type=float, default=120)
    parser.add_argument("--timeout", type=float, default=120)
    args = parser.parse_args(argv)
    try:
        notes = tuple(int(value) for value in args.notes.split(","))
        validate_options(notes, args.velocity, args.sample_rate, args.hold, args.tail, args.bpm, args.timeout)
        args.patch = args.patch.expanduser()
        folder = args.patch.is_dir() and args.patch.suffix.lower() != ".patch"
        if args.recursive or args.output_dir or folder:
            from logic_patch_batch import render_batch
            if args.output:
                raise PreviewError("Batch mode uses --output-dir, not --output.")
            output_dir = args.output_dir or PROJECT / "artifacts/logic-preview/batch" / (args.patch.stem or "library")
            def progress(index, total, entry):
                print(f"[{index}/{total}] {entry['status']}: {entry['relative_source']}", file=sys.stderr)
                if entry["status"] == "failed":
                    print("  " + entry["message"], file=sys.stderr)
            report = render_batch(args.patch, output_dir, args.worker, render_one=render_preview, progress=progress,
                                  au_chain_only=args.au_chain_only, notes=notes, velocity=args.velocity,
                                  sample_rate=args.sample_rate, hold=args.hold, tail=args.tail,
                                  bpm=args.bpm, timeout=args.timeout)
            if args.json:
                print(json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False))
            else:
                print(f"Batch finished: {report['rendered']} rendered, {report['skipped_existing']} existing preserved, "
                      f"{report['failed']} failed, {report['discovery_failed']} discovery errors, {report['unrun']} unrun.")
                print("Report: " + report["report_path"])
                print("Batch previews are saved without playback. Logic equivalence is unverified.")
            return 130 if report["cancelled"] else 1 if report["failed"] or report["discovery_failed"] else 0
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
        name = re.sub(r"[^\w.-]", "_", args.patch.stem)[:80] or "patch"
        output = args.output or PROJECT / "artifacts/logic-preview" / f"{name}-{stamp}.wav"
        report = render_preview(args.patch, output, args.worker, au_chain_only=args.au_chain_only, notes=notes,
                                velocity=args.velocity, sample_rate=args.sample_rate, hold=args.hold,
                                tail=args.tail, bpm=args.bpm, timeout=args.timeout)
        if args.json:
            print(json.dumps(report, indent=2, ensure_ascii=False, allow_nan=False))
        else:
            print("AU-chain preview saved: " + report["output"])
            print("Logic equivalence is unverified.")
            for message in report["approximations"]:
                print("Approximation: " + message)
        if args.play:
            try:
                subprocess.run(["/usr/bin/afplay", report["output"]], check=True, timeout=90)
            except (OSError, subprocess.SubprocessError) as error:
                print(f"Playback failed; the rendered WAV is preserved: {error}", file=sys.stderr)
                return 1
        return 0
    except (PreviewError, inspector.InspectionError, OSError, ValueError, wave.Error, subprocess.SubprocessError) as error:
        print(f"Logic preview failed: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Logic preview cancelled.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
