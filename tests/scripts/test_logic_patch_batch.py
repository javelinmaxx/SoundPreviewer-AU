"""Recursive discovery, preservation, progress and saved batch report contracts."""
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import logic_patch_batch as batch


class LogicPatchBatchTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.source = self.base / "library"
        self.source.mkdir()
        self.output = self.base / "saved"
        self.worker = self.base / "worker"
        self.worker.write_text("TEST ONLY worker placeholder")
        self.worker.chmod(0o755)

    def patch(self, relative):
        path = self.source / relative
        path.mkdir(parents=True)
        return path

    def render(self, **kwargs):
        def renderer(source, target, worker, **options):
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"TEST ONLY output sentinel, not playable audio")
            return {"output": str(target), "options": options}
        with mock.patch.object(batch.sys, "platform", "darwin"):
            return batch.render_batch(self.source, self.output, self.worker, render_one=renderer, **kwargs)

    def test_recursive_discovery_prunes_package_contents_and_output_tree(self):
        first = self.patch("A/first.patch")
        self.patch("B/deep/second.PATCH")
        (first / "nested.patch").mkdir()
        inside_output = self.source / "saved"
        (inside_output / "ignored.patch").mkdir(parents=True)
        _, found, issues = batch.discover_patches(self.source, inside_output)
        self.assertEqual(["A/first.patch", "B/deep/second.PATCH"], [p.relative_to(self.source).as_posix() for p in found])
        self.assertEqual([], issues)

    def test_output_ancestor_does_not_exclude_the_whole_library(self):
        self.patch("Group/first.patch")
        self.assertEqual(1, len(batch.discover_patches(self.source, self.base)[1]))

    def test_saves_relative_names_and_reports_and_preserves_existing_outputs(self):
        self.patch("A/Pad.patch")
        self.patch("B/Pad.patch")
        report = self.render(notes=(60, 84, 36))
        self.assertEqual(2, report["rendered"])
        self.assertTrue((self.output / "A/Pad.wav").is_file())
        self.assertTrue((self.output / "B/Pad.wav").is_file())
        self.assertEqual(2, len(Path(report["journal_path"]).read_text().splitlines()))
        self.assertEqual(2, json.loads(Path(report["report_path"]).read_text())["rendered"])
        (self.output / "A/Pad.wav").write_bytes(b"existing audio")
        repeated = self.render()
        self.assertEqual(2, repeated["skipped_existing"])
        self.assertEqual(b"existing audio", (self.output / "A/Pad.wav").read_bytes())
        self.assertNotEqual(report["report_path"], repeated["report_path"])

    def test_a_failure_does_not_stop_other_patches(self):
        self.patch("bad.patch")
        self.patch("good.patch")
        def renderer(source, target, worker, **options):
            if source.name == "bad.patch":
                raise ValueError("TEST ONLY unsupported preset")
            target.write_bytes(b"TEST ONLY sentinel")
            return {}
        with mock.patch.object(batch.sys, "platform", "darwin"):
            report = batch.render_batch(self.source, self.output, self.worker, render_one=renderer)
        self.assertEqual((1, 1), (report["rendered"], report["failed"]))
        self.assertIn("unsupported", report["results"][0]["message"])

    def test_single_patch_and_no_matches(self):
        with self.assertRaisesRegex(batch.BatchError, "No .patch"):
            self.render()
        source = self.patch("solo.patch")
        with mock.patch.object(batch.sys, "platform", "darwin"):
            report = batch.render_batch(source, self.output, self.worker, render_one=lambda *args, **kwargs: {})
        self.assertEqual(1, report["total"])
        self.assertEqual(str(self.output / "solo.wav"), report["results"][0]["output"])

    def test_case_collisions_fail_without_overwriting_either_preview(self):
        self.patch("one.patch")
        candidate = self.source / "ONE.patch"
        if candidate.exists():
            self.skipTest("The source filesystem already prevents case-only duplicate names")
        candidate.mkdir()
        report = self.render()
        self.assertEqual(2, report["failed"])
        self.assertEqual([], list(self.output.glob("*.wav")))

    def test_cancellation_preserves_progress_and_unrun_count(self):
        self.patch("first.patch")
        self.patch("second.patch")
        cancel = threading.Event()
        def renderer(source, target, worker, **options):
            self.assertIs(cancel, options["cancel_event"])
            raise KeyboardInterrupt()
        with mock.patch.object(batch.sys, "platform", "darwin"):
            report = batch.render_batch(self.source, self.output, self.worker, render_one=renderer, cancel_event=cancel)
        self.assertTrue(report["cancelled"])
        self.assertEqual(1, report["unrun"])
        self.assertTrue(Path(report["report_path"]).is_file())

    def test_links_are_skipped_and_output_inside_a_package_is_rejected(self):
        patch = self.patch("first.patch")
        try:
            (self.source / "linked.patch").symlink_to(patch, target_is_directory=True)
        except OSError:
            self.skipTest("Symlink creation unavailable")
        _, found, issues = batch.discover_patches(self.source, self.output)
        self.assertEqual(1, len(found))
        self.assertEqual("skipped_link", issues[0]["status"])
        with mock.patch.object(batch.sys, "platform", "darwin"), self.assertRaises(batch.BatchError):
            batch.render_batch(self.source, patch / "outputs", self.worker, render_one=lambda *args: {})


if __name__ == "__main__":
    unittest.main()
