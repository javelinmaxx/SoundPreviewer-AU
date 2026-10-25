#!/usr/bin/env python3
"""Bridge a desktop render request to the checked Logic AU-chain renderer."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys

import logic_patch_preview as preview


def package_identity(patch: Path) -> str:
    """Match LogicPatchSource: names plus member SHA256s, including bundled assets."""
    patch = preview.inspector.resolve_patch_path(patch)
    files = []
    members = 0
    def fail(error):
        raise error

    for root, directories, names in os.walk(patch, followlinks=False, onerror=fail):
        for name in directories + names:
            member = Path(root) / name
            members += 1
            if members > 4096 or member.is_symlink():
                raise preview.PreviewError("Patch has linked members or exceeds 4096 members.")
        files.extend(Path(root) / name for name in names)
    digest = hashlib.sha256()
    total = 0
    # .NET ordinal ordering compares UTF-16 code units.
    files.sort(key=lambda member: member.relative_to(patch).as_posix().encode("utf-16-be"))
    for member in files:
        before = member.stat()
        total += before.st_size
        if total > 256 * 1024 * 1024 or not member.is_file():
            raise preview.PreviewError("Patch exceeds 256 MiB or contains a non-file member.")
        content = hashlib.sha256()
        with member.open("rb") as source:
            while chunk := source.read(65536):
                content.update(chunk)
        after = member.stat()
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise preview.PreviewError("Patch changed while reading. Rescan before rendering.")
        name = member.relative_to(patch).as_posix()
        digest.update((name + "\0" + content.hexdigest() + "\0").encode("utf-8"))
    return digest.hexdigest()


def render_request(request: dict) -> dict:
    preset, plugin, template = request["Preset"], request["Plugin"], request["Template"]
    if request.get("IsInitializationBaseline") or preset["Synth"] != 6 or plugin["Synth"] != 6 or plugin["Backend"] != 5:
        raise preview.PreviewError("Logic Patch bridge only accepts matching .patch requests, not startup baselines.")
    if preset.get("InternalPath") or preset.get("BankSlot") is not None:
        raise preview.PreviewError("Logic Patch must be a complete package, not a bank entry.")
    patch = Path(preset["SourcePath"])
    expected = preset["ContentHash"]
    if len(expected) != 64 or package_identity(patch) != expected:
        raise preview.PreviewError("Logic Patch changed since scanning. Rescan before rendering.")
    allow = plugin.get("AllowLogicPatchApproximation", True)
    if type(allow) is not bool:
        raise preview.PreviewError("AU-chain approximation must be an explicit checkbox value.")
    notes = ([template["LowNote"], template["MiddleNote"], template["HighNote"]]
             if template.get("NoteOrder", 0) == 0 else
             [template["MiddleNote"], template["HighNote"], template["LowNote"]])
    hold, tail = template["HoldSeconds"], template["TailSeconds"]
    output = Path(request["OutputPath"])
    created = False
    try:
        report = preview.render_preview(patch, output, Path(plugin["ExecutablePath"]), au_chain_only=allow,
            notes=tuple(notes), velocity=template["Velocity"], sample_rate=template["SampleRate"],
            hold=hold, tail=tail, bpm=template["Bpm"], timeout=plugin["TimeoutSeconds"],
            inherit_process_group=True)
        created = True
        if package_identity(patch) != expected:
            raise preview.PreviewError("Logic Patch changed during rendering. Rescan before rendering.")
        if output.stat().st_size > request.get("MaxOutputBytes", 256 * 1024 * 1024):
            raise preview.PreviewError("Preview exceeds the reserved output size.")
        sample_rate = template["SampleRate"]
        frames = math.floor(hold * sample_rate + 0.5) + math.floor(tail * sample_rate + 0.5)
        return {"Success": True, "OutputPath": str(output.absolute()), "NeedsReview": True,
                "AdapterVersion": "logic-au-chain-desktop-v1",
                "Notes": [{"MidiNote": pitch, "StartSeconds": index * frames / sample_rate,
                           "DurationSeconds": frames / sample_rate, "ActualHoldSeconds": hold,
                           "ActualTailSeconds": tail, "ActualVelocity": template["Velocity"]}
                          for index, pitch in enumerate(notes)],
                "Message": "Logic AU 链已生成；与 Logic 完整通道条等效性未验证。" +
                           " ".join(report["approximations"])}
    except preview.PatchSkipped as error:
        return {"Success": False, "NonRetryable": True, "SkippedUnsupported": True,
                "AdapterVersion": "logic-au-chain-desktop-v1", "Message": str(error)}
    except BaseException:
        if created:
            output.unlink(missing_ok=True)
        raise


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        if sys.version_info < (3, 10):
            raise preview.PreviewError("Python 3.10+ is required. Set SOUNDPREVIEWER_PYTHON to its executable.")
        if args.request.stat().st_size > 1024 * 1024:
            raise preview.PreviewError("Desktop request exceeds 1 MiB.")
        result = render_request(json.loads(args.request.read_text(encoding="utf-8")))
        print(json.dumps(result, ensure_ascii=False, allow_nan=False))
        return 0
    except (ValueError, OSError, KeyError, TypeError) as error:
        print("Logic Patch: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
