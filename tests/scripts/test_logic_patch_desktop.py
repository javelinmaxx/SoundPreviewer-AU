"""Desktop bridge contracts using synthetic workers; native DSP is checked on Macs."""
import copy
import os
from pathlib import Path
import plistlib
import sys
import unittest
from unittest import mock

import test_logic_patch_preview as preview_tests
from test_logic_patch_inspector import block, pst_state

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import logic_patch_desktop as desktop


class DesktopBridgeTests(unittest.TestCase):
    def setUp(self):
        self.fixture = preview_tests.LogicPatchPreviewTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.request = {"Preset": {"SourcePath": str(self.fixture.root), "Synth": 6,
                                   "ContentHash": desktop.package_identity(self.fixture.root)},
                        "Plugin": {"Synth": 6, "Backend": 5, "ExecutablePath": str(self.fixture.fake_worker()),
                                   "TimeoutSeconds": 5},
                        "Template": {"LowNote": 36, "MiddleNote": 60, "HighNote": 84, "NoteOrder": 1,
                                     "HoldSeconds": 0.05, "TailSeconds": 0, "Velocity": 89,
                                     "SampleRate": 44100, "Bpm": 120},
                        "OutputPath": str(self.fixture.output)}

    def render(self):
        with mock.patch.object(desktop.preview.sys, "platform", "darwin"):
            return desktop.render_request(self.request)

    def test_desktop_request_renders_and_maps_three_registers(self):
        result = self.render()
        self.assertTrue(result["Success"])
        self.assertTrue(result["NeedsReview"])
        self.assertEqual([60, 84, 36], [note["MidiNote"] for note in result["Notes"]])
        self.assertEqual([0, 0.05, 0.1], [note["StartSeconds"] for note in result["Notes"]])
        self.assertEqual("logic-au-chain-desktop-v1", result["AdapterVersion"])
        self.assertTrue(self.fixture.output.is_file())

    def test_named_binary_native_style_states_are_readable(self):
        from test_logic_patch_inspector import au_state
        self.fixture.write([block(plistlib.dumps(au_state(), fmt=plistlib.FMT_BINARY)),
                            block(plistlib.dumps(au_state("aufx", "Fx01"), fmt=plistlib.FMT_BINARY), slot=3, role=0)])
        self.request["Preset"]["ContentHash"] = desktop.package_identity(self.fixture.root)
        self.assertTrue(self.render()["Success"])

    def test_changed_source_and_mismatched_backend_never_render(self):
        original = copy.deepcopy(self.request)
        for key, value in (("IsInitializationBaseline", True),):
            self.request[key] = value
            with self.assertRaises(ValueError):
                self.render()
            self.request = copy.deepcopy(original)
        self.request["Plugin"]["Backend"] = 3
        with self.assertRaises(ValueError):
            self.render()
        self.request = original
        (self.fixture.root / "new-asset.bin").write_bytes(b"changed package")
        with self.assertRaisesRegex(ValueError, "changed since scanning"):
            self.render()
        self.assertFalse(self.fixture.output.exists())

    def test_opaque_metadata_needs_explicit_approximation(self):
        from test_logic_patch_inspector import au_state
        self.fixture.write([block(plistlib.dumps(au_state())),
                            block(plistlib.dumps({"$archiver": "NSKeyedArchiver"}), direct=True, slot=9)])
        self.request["Preset"]["ContentHash"] = desktop.package_identity(self.fixture.root)
        self.request["Plugin"]["AllowLogicPatchApproximation"] = False
        with self.assertRaisesRegex(ValueError, "au-chain-only"):
            self.render()
        self.request["Plugin"]["AllowLogicPatchApproximation"] = True
        self.assertTrue(self.render()["NeedsReview"])

    def test_native_pst_never_bypassed_by_approximation(self):
        self.fixture.write([block(pst_state())])
        self.request["Preset"]["ContentHash"] = desktop.package_identity(self.fixture.root)
        self.request["Plugin"]["AllowLogicPatchApproximation"] = True
        result = self.render()
        self.assertFalse(result["Success"])
        self.assertTrue(result["NonRetryable"])
        self.assertTrue(result["SkippedUnsupported"])
        self.assertFalse(self.fixture.output.exists())

    def test_publish_checks_reserved_size_and_preserves_existing_audio(self):
        self.request["MaxOutputBytes"] = 1
        with self.assertRaisesRegex(ValueError, "reserved output size"):
            self.render()
        self.assertFalse(self.fixture.output.exists())
        self.fixture.output.write_bytes(b"existing")
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.render()
        self.assertEqual(b"existing", self.fixture.output.read_bytes())

    def test_changed_during_render_removes_only_new_output(self):
        original = desktop.preview.render_preview

        def change(*args, **kwargs):
            report = original(*args, **kwargs)
            (self.fixture.root / "data.plist").write_bytes(b"changed")
            return report

        with mock.patch.object(desktop.preview, "render_preview", side_effect=change):
            with self.assertRaisesRegex(ValueError, "changed during rendering"):
                self.render()
        self.assertFalse(self.fixture.output.exists())

    @unittest.skipIf(os.name == "nt", "Unix link contract")
    def test_identity_rejects_linked_package_members(self):
        (self.fixture.root / "linked").symlink_to(self.fixture.root / "data.plist")
        with self.assertRaisesRegex(ValueError, "linked members"):
            desktop.package_identity(self.fixture.root)


if __name__ == "__main__":
    unittest.main()
