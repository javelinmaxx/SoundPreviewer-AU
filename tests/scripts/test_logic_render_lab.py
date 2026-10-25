"""Research harness contracts; synthetic WAVs do not test Logic or its instruments."""
import contextlib
import io
import json
import math
from pathlib import Path
import plistlib
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import wave

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import logic_render_lab as lab


class LogicRenderLabTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.workspace = self.root / "TEST ONLY experiment"

    def prepare(self):
        return lab.prepare(self.workspace, "TEST ONLY factory name", hold=0.05, tail=0.01)

    def bounce(self, request, *, silent=None, rate=44100, channels=2, width=2, seconds=None):
        path = self.root / "TEST ONLY bounced.wav"
        frames = round((seconds or request["timing"]["expected_seconds"]) * rate)
        ranges = [(round(note["start_seconds"] * rate), round((note["start_seconds"] + note["hold_seconds"]) * rate))
                  for note in request["timing"]["notes"]]
        samples = bytearray()
        for index in range(frames):
            audible = any(start <= index < end and slot != silent for slot, (start, end) in enumerate(ranges))
            value = round(math.sin(index * 2 * math.pi * 440 / rate) * (1 << (width * 8 - 3))) if audible else 0
            samples.extend(value.to_bytes(width, "little", signed=True) * channels)
        with wave.open(str(path), "wb") as output:
            output.setparams((channels, width, rate, frames, "NONE", "not compressed"))
            output.writeframes(samples)
        return path

    def test_standard_midi_events_and_leading_bar(self):
        midi, timing = lab.make_midi()
        self.assertEqual((6, 0, 1, 960), struct.unpack(">IHHH", midi[4:14]))
        self.assertEqual(b"MTrk", midi[14:18])
        self.assertEqual(len(midi) - 22, int.from_bytes(midi[18:22], "big"))
        position, ticks, notes, commands = 22, 0, [], []

        def variable():
            nonlocal position
            value = 0
            while True:
                byte = midi[position]
                position += 1
                value = (value << 7) | (byte & 127)
                if byte < 128:
                    return value

        while position < len(midi):
            ticks += variable()
            command = midi[position]
            position += 1
            commands.append(command)
            if command == 255:
                kind = midi[position]
                position += 1
                length = variable()
                if kind == 81:
                    self.assertEqual(500000, int.from_bytes(midi[position:position + length], "big"))
                position += length
            else:
                if command == 144:
                    notes.append((ticks, midi[position], midi[position + 1]))
                position += 2
        self.assertEqual([(3840, 60, 89), (6720, 84, 89), (9600, 36, 89)], notes)
        self.assertEqual(12480, ticks)
        self.assertNotIn(192, commands)  # Never change Logic's selected instrument/program.
        self.assertEqual(6.5, timing["expected_seconds"])

    def test_invalid_midi_parameters_create_no_workspace(self):
        for settings in ({"notes": (60, 84)}, {"velocity": 0}, {"bpm": 0}, {"hold": float("nan")}, {"tail": -1}):
            with self.subTest(settings=settings), self.assertRaises(ValueError):
                lab.prepare(self.workspace, "TEST ONLY", **settings)
        self.assertFalse(self.workspace.exists())

    def test_workspace_and_template_are_preserved(self):
        template = self.root / "TEST ONLY template.logicx"
        (template / "Alternatives/000").mkdir(parents=True)
        source = template / "Alternatives/000/ProjectData"
        source.write_bytes(b"TEST ONLY placeholder; not an actual Logic project")
        before = source.stat().st_mtime_ns
        request = lab.prepare(self.workspace, "TEST ONLY", template)
        self.assertTrue(request["template_copied"])
        self.assertEqual(source.read_bytes(), (self.workspace / "Session.logicx/Alternatives/000/ProjectData").read_bytes())
        self.assertEqual(before, source.stat().st_mtime_ns)
        self.assertEqual("12.0", request["target_logic_version"])
        self.assertIn("manual", (self.workspace / "STEPS.txt").read_text())
        with self.assertRaises(ValueError):
            lab.prepare(self.workspace, "TEST ONLY")
        self.assertTrue((self.workspace / "Preview.mid").exists())

    @unittest.skipIf(sys.platform == "win32", "Unix symlink contract")
    def test_linked_templates_are_rejected_before_any_copy(self):
        template = self.root / "TEST ONLY linked.logicx"
        template.mkdir()
        outside = self.root / "outside"
        outside.write_bytes(b"preserve")
        (template / "asset").symlink_to(outside)
        with self.assertRaisesRegex(ValueError, "Linked"):
            lab.prepare(self.workspace, "TEST ONLY", template)
        self.assertFalse(self.workspace.exists())
        self.assertEqual(b"preserve", outside.read_bytes())

    def test_inventory_prunes_packages_and_deduplicates_roots(self):
        patch = self.root / "Patches/Instrument/TEST ONLY.patch"
        (patch / "internal.patch").mkdir(parents=True)
        report = lab.inventory([self.root / "Patches", patch, self.root / "missing"])
        self.assertEqual([{"name": "TEST ONLY", "path": str(patch)}], report["patches"])
        self.assertFalse(report["complete_factory_catalog_verified"])
        self.assertFalse(report["installed_sound_assets_verified"])

    def test_collect_preserves_pcm_and_marks_identity_unverified(self):
        request = self.prepare()
        bounce = self.bounce(request)
        before = bounce.read_bytes()
        result = lab.collect(self.workspace, bounce, request["patch_name"])
        self.assertEqual(before, (self.workspace / "preview.wav").read_bytes())
        self.assertEqual(before, bounce.read_bytes())
        self.assertFalse(result["preset_identity_verified"])
        self.assertFalse(result["automated_patch_selection"])
        self.assertFalse(result["timing_verified"])
        self.assertTrue(result["needs_review"])
        self.assertEqual(3, len(result["metrics"]["note_rms"]))
        with self.assertRaisesRegex(ValueError, "already"):
            lab.collect(self.workspace, bounce, request["patch_name"])

    def test_collect_supports_logics_24_bit_pcm_bounce(self):
        request = self.prepare()
        result = lab.collect(self.workspace, self.bounce(request, width=3), request["patch_name"])
        self.assertEqual(24, result["metrics"]["bits"])

    def test_silent_note_window_never_publishes(self):
        request = self.prepare()
        with self.assertRaisesRegex(ValueError, "silent"):
            lab.collect(self.workspace, self.bounce(request, silent=1), request["patch_name"])
        self.assertFalse((self.workspace / "preview.wav").exists())

    def test_wrong_bounce_shape_never_publishes(self):
        request = self.prepare()
        for options in ({"rate": 48000}, {"channels": 1}, {"seconds": 1}, {"seconds": 20}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                lab.collect(self.workspace, self.bounce(request, **options), request["patch_name"])
        self.assertFalse((self.workspace / "preview.wav").exists())

    def test_patch_confirmation_and_midi_fingerprint_are_required(self):
        request = self.prepare()
        bounce = self.bounce(request)
        with self.assertRaisesRegex(ValueError, "exact"):
            lab.collect(self.workspace, bounce, "wrong patch")
        (self.workspace / "Preview.mid").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "MIDI file changed"):
            lab.collect(self.workspace, bounce, request["patch_name"])

    def test_publish_race_does_not_replace_result_or_leave_partial_audio(self):
        request = self.prepare()
        bounce = self.bounce(request)
        link = lab.os.link

        def race(source, destination):
            if Path(destination).name == "result.json":
                Path(destination).write_text("preserve racing writer")
            return link(source, destination)

        with mock.patch.object(lab.os, "link", side_effect=race), self.assertRaises(FileExistsError):
            lab.collect(self.workspace, bounce, request["patch_name"])
        self.assertFalse((self.workspace / "preview.wav").exists())
        self.assertEqual("preserve racing writer", (self.workspace / "result.json").read_text())

    def test_probe_extracts_dictionary_without_launching_logic(self):
        application = self.root / "TEST ONLY Logic.app"
        (application / "Contents").mkdir(parents=True)
        (application / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": "com.apple.logic10", "CFBundleShortVersionString": "12.0"}))
        definition = SimpleNamespace(returncode=0, stdout=b'<dictionary><suite><command name="open"/><command name="bounce"/></suite></dictionary>', stderr=b"")
        with mock.patch.object(lab.sys, "platform", "darwin"), mock.patch.object(lab.subprocess, "run", return_value=definition) as run:
            result = lab.probe(application)
        self.assertTrue(result["target_version_match"])
        self.assertFalse(result["logic_launched"])
        self.assertEqual([{"name": "bounce"}], result["render_related_command_candidates"])
        self.assertEqual(["/usr/bin/sdef", str(application)], run.call_args.args[0])
        self.assertEqual(1, run.call_count)

    def test_cli_prepare_runs_without_logic_and_reports_errors(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, lab.main(["prepare", "--workspace", str(self.workspace), "--name", "TEST ONLY"]))
        self.assertEqual("awaiting_manual_logic_bounce", json.loads(output.getvalue())["status"])
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, lab.main(["prepare", "--workspace", str(self.workspace), "--name", "TEST ONLY"]))


if __name__ == "__main__":
    unittest.main()
