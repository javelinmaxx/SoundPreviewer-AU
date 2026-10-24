"""Recursive batch orchestration; the existing preview worker owns each render."""
from __future__ import annotations

import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import unicodedata
import uuid
import wave

import logic_patch_inspector as inspector


class BatchError(ValueError):
    pass


def discover_patches(source: Path, output_dir: Path) -> tuple[Path, list[Path], list[dict]]:
    """Prune package contents and output folders; never follow directory links."""
    source = source.expanduser().absolute()
    if source.suffix.lower() == ".patch" or source.is_file():
        patch = inspector.resolve_patch_path(source)
        return patch.parent, [patch], []
    if source.is_symlink() or not source.is_dir():
        raise inspector.InspectionError(f"Expected an existing patch or unlinked library folder: {str(source)!r}")
    patches, issues = [], []
    excluded = output_dir.resolve()
    exclude_output = excluded != source.resolve() and excluded.is_relative_to(source.resolve())

    def error(problem):
        issues.append({"status": "discovery_failed", "source": str(problem.filename), "message": str(problem)})

    for directory, folders, files in os.walk(source, topdown=True, followlinks=False, onerror=error):
        parent = Path(directory)
        descend = []
        for name in sorted(folders, key=lambda value: (value.casefold(), value)):
            child = parent / name
            if child.is_symlink():
                issues.append({"status": "skipped_link", "source": str(child), "message": "Directory links are not followed."})
            elif exclude_output and child.resolve().is_relative_to(excluded):
                continue
            elif child.suffix.lower() == ".patch":
                patches.append(child)
            else:
                descend.append(name)
        folders[:] = descend
        for name in sorted(files, key=lambda value: (value.casefold(), value)):
            child = parent / name
            if child.suffix.lower() == ".patch":
                if child.is_symlink():
                    issues.append({"status": "skipped_link", "source": str(child), "message": "Patch links are not followed."})
                else:
                    # A flat .patch may be an unsupported format. Keep it as a
                    # candidate so its individual failure is visible, not lost.
                    patches.append(child)
    return source, sorted(patches, key=lambda path: (str(path.relative_to(source)).casefold(), str(path))), issues


def render_batch(source: Path, output_dir: Path, worker: Path, *, render_one, progress=None,
                 on_discovered=None, cancel_event=None, **options) -> dict:
    """Serial fresh-instance renders, preserving completed outputs and a journal."""
    if sys.platform != "darwin":
        raise BatchError("AU batch rendering requires macOS.")
    output_dir = output_dir.expanduser().absolute()
    for candidate in (output_dir, output_dir.resolve()):
        if any(parent.suffix.lower() == ".patch" for parent in (candidate, *candidate.parents)):
            raise BatchError("Batch output must be outside every .patch package.")
    base, patches, issues = discover_patches(source, output_dir)
    if not patches and not issues:
        raise BatchError(f"No .patch packages found under {str(source)!r}.")
    worker = worker.expanduser().absolute()
    if not worker.is_file() or not os.access(worker, os.X_OK):
        raise BatchError("Native worker is missing. Run bash scripts/Setup-LogicPreview.sh first.")
    if on_discovered:
        on_discovered(len(patches), issues)
    targets = [output_dir / path.relative_to(base).with_suffix(".wav") for path in patches]
    by_name = {}
    for index, target in enumerate(targets):
        key = unicodedata.normalize("NFD", str(target.relative_to(output_dir))).casefold()
        by_name.setdefault(key, []).append(index)
    collisions = {index for indices in by_name.values() if len(indices) > 1 for index in indices}
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    name = f"batch-report-{stamp}-{uuid.uuid4().hex[:8]}"
    report_path = output_dir / (name + ".json")
    journal_path = output_dir / (name + ".jsonl")
    report = {"schema_version": 1, "source": str(source.expanduser().absolute()), "output_dir": str(output_dir),
              "report_path": str(report_path), "journal_path": str(journal_path), "total": len(patches),
              "scope": "recognized_au_chain_only", "logic_equivalence_verified": False,
              "options": options, "discovery_issues": issues, "results": [], "cancelled": False}
    expected_errors = (inspector.InspectionError, OSError, ValueError, wave.Error, subprocess.SubprocessError)
    try:
        with journal_path.open("x", encoding="utf-8") as journal:
            for index, (patch, target) in enumerate(zip(patches, targets)):
                if cancel_event is not None and cancel_event.is_set():
                    report["cancelled"] = True
                    break
                entry = {"source": str(patch), "relative_source": str(patch.relative_to(base)), "output": str(target)}
                try:
                    if index in collisions:
                        raise BatchError("Output name collides with another patch after Mac case/Unicode normalization.")
                    if target.is_symlink() or (target.exists() and not target.is_file()):
                        raise BatchError("Output is a link or non-file; it was preserved.")
                    if target.is_file():
                        entry.update(status="skipped_existing", message="Existing output preserved; it was not revalidated against the current patch.")
                    else:
                        controls = {"cancel_event": cancel_event} if cancel_event is not None else {}
                        result = render_one(patch, target, worker, **controls, **options)
                        entry.update(status="rendered", result=result)
                except KeyboardInterrupt:
                    entry.update(status="cancelled", message="Rendering cancelled; earlier completed files were preserved.")
                    report["cancelled"] = True
                except expected_errors as error:
                    entry.update(status="failed", message=str(error))
                report["results"].append(entry)
                journal.write(json.dumps(entry, ensure_ascii=False, allow_nan=False) + "\n")
                journal.flush()
                if progress:
                    progress(index + 1, len(patches), entry)
                if report["cancelled"]:
                    break
    finally:
        report["rendered"] = sum(entry["status"] == "rendered" for entry in report["results"])
        report["skipped_existing"] = sum(entry["status"] == "skipped_existing" for entry in report["results"])
        report["failed"] = sum(entry["status"] == "failed" for entry in report["results"])
        report["discovery_failed"] = sum(issue["status"] == "discovery_failed" for issue in issues)
        report["unrun"] = len(patches) - len(report["results"])
        # Each run owns a unique report; prior reports and audio are not replaced.
        with report_path.open("x", encoding="utf-8") as destination:
            json.dump(report, destination, indent=2, ensure_ascii=False, allow_nan=False)
            destination.write("\n")
    return report
