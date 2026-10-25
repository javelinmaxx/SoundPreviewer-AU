"""Synthetic container contracts: no Logic install, commercial state or plug-in execution."""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/logic_patch_inspector.py"
SPEC = importlib.util.spec_from_file_location("logic_patch_inspector", SCRIPT)
inspector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(inspector)


def fourcc(text):
    return int.from_bytes(text.encode("ascii"), "big")


def au_state(kind="aumu", subtype="Test", manufacturer="TEST"):
    return {"type": fourcc(kind), "subtype": fourcc(subtype), "manufacturer": fourcc(manufacturer),
            "name": "TEST ONLY state", "data": b"synthetic state, never executable"}


def pst_state(payload=b"test only"):
    return struct.pack("<III", len(payload) + 24, 1, len(payload)) + b"GAMETSPP" + struct.pack("<I", 0) + payload


def block(payload, slot=2, role=8, direct=False, offset=208):
    prefix = bytearray(56 if direct else offset)
    prefix[:4] = b"UCuA"
    struct.pack_into("<H", prefix, 18, slot)
    struct.pack_into("<I", prefix, 36, 64 if direct else 424)
    if direct:
        struct.pack_into("<I", prefix, 52, len(payload))
    else:
        prefix[50:65] = b"TEST preset.pst"
        prefix[156:165] = b"TEST ONLY"
        struct.pack_into("<I", prefix, 176, 0x117)
        prefix[187] = role
        if payload.startswith((b"<?xml", b"bplist00")):
            value = plistlib.loads(payload)
            for key, address in (("manufacturer", 168), ("type", 172), ("subtype", 176)):
                if key in value:
                    struct.pack_into("<I", prefix, address, value[key])
    if payload.startswith(b"bplist00"):
        # Named AU wrappers, like direct wrappers, declare the binary plist
        # length immediately before its payload. Native fullState is binary.
        struct.pack_into("<I", prefix, len(prefix) - 4, len(payload))
    data = prefix + payload
    struct.pack_into("<I", data, 28, len(data) - 36)
    return bytes(data)


def cst(blocks=(), channel_type=0x43):
    header = bytearray(237)
    header[:4] = b"OCuA"
    header[40] = channel_type
    struct.pack_into("<I", header, 28, len(header) - 36)
    return bytes(header) + b"".join(blocks) + b"OCuA" + bytes(46)


class LogicPatchInspectorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "TEST ONLY library.patch"
        self.root.mkdir()
        self.metadata = {"VersionPatches": 40014, "unknown_future_key": {"preserve": True},
                         "channels": [{"Filename": "#Root.cst", "Root": True, "Channel_name": "Test library",
                                       "Channel_inputIndex_1": -1, "Channel_outputIndex": 0,
                                       "Channel_outputIsBus": False, "Channel_sends": [{}, {}]}]}
        self.write_metadata()
        self.write_cst(blocks=[block(plistlib.dumps(au_state()))])

    def write_metadata(self, fmt=plistlib.FMT_BINARY):
        (self.root / "data.plist").write_bytes(plistlib.dumps(self.metadata, fmt=fmt))

    def write_cst(self, name="#Root.cst", blocks=(), channel_type=0x43):
        (self.root / name).write_bytes(cst(blocks, channel_type))

    def snapshot(self):
        return {p.name: (hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_mtime_ns)
                for p in self.root.iterdir() if p.is_file()}

    def test_reads_xml_and_binary_metadata_without_writes(self):
        for fmt in (plistlib.FMT_BINARY, plistlib.FMT_XML):
            self.write_metadata(fmt)
            before = self.snapshot()
            report = inspector.inspect_patch(self.root)
            self.assertEqual(before, self.snapshot())
            self.assertFalse(report["rendering_supported"])
            self.assertEqual({"preserve": True}, report["metadata"]["unknown_future_key"])
            self.assertEqual("not_checked", report["channels"][0]["plugins"][0]["availability"])

    def test_classifies_ordered_instrument_midi_and_audio_effects(self):
        blocks = [block(pst_state(b"nested <?xml is not a second plug-in"), role=0x18),
                  block(plistlib.dumps(au_state("aufx")), slot=3, role=0x10),
                  block(pst_state(), slot=7, role=0x02)]
        self.write_cst(blocks=blocks)
        plugins = inspector.inspect_patch(self.root)["channels"][0]["plugins"]
        self.assertEqual(["instrument", "audio_fx", "midi_fx"], [p["role"] for p in plugins])
        self.assertEqual([2, 3, 7], [p["slot"] for p in plugins])
        self.assertIsNone(plugins[0]["component"])
        self.assertEqual("logic_native_hostability_unknown", plugins[0]["availability"])
        self.assertEqual(0x117, plugins[0]["logic_plugin_id"])

    def test_variable_named_prefix_is_not_a_fixed_offset_guess(self):
        self.write_cst(blocks=[block(pst_state(), offset=396)])
        self.assertEqual("logic_pst", inspector.inspect_patch(self.root)["channels"][0]["plugins"][0]["state_format"])

    def test_direct_binary_au_uses_component_type_instead_of_payload_byte_187(self):
        state = plistlib.dumps(au_state("aufx"), fmt=plistlib.FMT_BINARY)
        self.write_cst(blocks=[block(state, direct=True)])
        plugin = inspector.inspect_patch(self.root)["channels"][0]["plugins"][0]
        self.assertEqual("audio_fx", plugin["role"])
        self.assertEqual("au_type", plugin["role_source"])
        self.assertIsNone(plugin["display_name"])

    def test_preserves_internal_archives_and_unknown_blocks_explicitly(self):
        archive = plistlib.dumps({"$archiver": "NSKeyedArchiver", "$objects": [plistlib.UID(0)]}, fmt=plistlib.FMT_BINARY)
        self.write_cst(blocks=[block(archive, slot=9, direct=True), block(b"unknown", slot=12)])
        channel = inspector.inspect_patch(self.root)["channels"][0]
        self.assertEqual([], channel["plugins"])
        self.assertEqual(["logic_internal_metadata", "unrecognized_block"], [b["kind"] for b in channel["opaque_blocks"]])
        self.assertTrue(channel["warnings"])

    def test_multi_channel_routing_and_nonempty_sends_remain_visible(self):
        additional = {"Filename": "Instrument.cst", "Channel_name": "Layer", "Channel_outputIsBus": True,
                      "Channel_outputIndex": 3, "Channel_sends": [{"unknown_send_key": 5}]}
        self.metadata["channels"].append(additional)
        self.write_metadata()
        self.write_cst("Instrument.cst", [block(pst_state())])
        report = inspector.inspect_patch(self.root)
        self.assertEqual(["#Root.cst", "Instrument.cst"], [c["filename"] for c in report["channels"]])
        self.assertEqual(additional, report["channels"][1]["settings"])
        self.assertTrue(any("populated sends" in warning for warning in report["warnings"]))

    def test_dependency_checks_use_exact_identity_not_preset_name(self):
        # A manufacturer may contain leading/trailing spaces, significant in a FourCC.
        for manufacturer in ("TEST", " kHs", "kHs "):
            self.write_cst(blocks=[block(plistlib.dumps(au_state(manufacturer=manufacturer)))])
            inventory = inspector.parse_au_inventory(f"    aumu Test {manufacturer}  -  TEST ONLY vendor: instrument\n")
            report = inspector.inspect_patch(self.root, inventory)
            self.assertEqual("registered", report["channels"][0]["plugins"][0]["availability"])
        missing = inspector.inspect_patch(self.root, {("aumu", "Test", "NOPE")})
        self.assertEqual("not_in_inventory", missing["channels"][0]["plugins"][0]["availability"])
        self.assertFalse(missing["rendering_supported"])

    def test_unreadable_inventory_cannot_label_everything_missing(self):
        for text in ("", "auval failed", "aumu Test TEST - Test\naufx incomplete\n"):
            with self.assertRaises(inspector.InspectionError):
                inspector.parse_au_inventory(text)

    def test_conflicting_wrapper_identity_is_never_a_registered_match(self):
        data = bytearray(block(plistlib.dumps(au_state())))
        struct.pack_into("<I", data, 176, fourcc("Else"))
        self.write_cst(blocks=[data])
        plugin = inspector.inspect_patch(self.root, {("aumu", "Test", "TEST")})["channels"][0]["plugins"][0]
        self.assertTrue(plugin["identity_conflict"])
        self.assertEqual("identity_conflict", plugin["availability"])
        self.assertEqual("Else", plugin["wrapper_component"]["subtype"])

    def test_au_inventory_file_cli_preserves_source_and_reports_missing(self):
        listing = Path(self.temporary.name) / "au inventory.txt"
        listing.write_text("aumu Else TEST  -  TEST ONLY vendor\n", encoding="utf-8")
        before = self.snapshot()
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(0, inspector.main([str(self.root), "--au-inventory", str(listing), "--json"]))
        self.assertEqual("not_in_inventory", json.loads(output.getvalue())["channels"][0]["plugins"][0]["availability"])
        self.assertEqual(before, self.snapshot())

    def test_orphan_strips_are_included_and_unknown_members_reported(self):
        self.write_cst("orphan.cst", [block(pst_state())])
        (self.root / "assets").mkdir()
        report = inspector.inspect_patch(self.root)
        self.assertEqual(2, len(report["channels"]))
        self.assertEqual(["assets"], report["uninspected_members"])
        self.assertTrue(any("routing is unknown" in warning for warning in report["warnings"]))

    def test_fingerprint_tracks_every_inspected_strip_and_metadata_but_not_location(self):
        first = inspector.inspect_patch(self.root)["inspected_state_sha256"]
        moved = Path(self.temporary.name) / "moved.patch"
        shutil.copytree(self.root, moved)
        self.assertEqual(first, inspector.inspect_patch(moved)["inspected_state_sha256"])
        self.metadata["unknown_future_key"] = False
        self.write_metadata()
        second = inspector.inspect_patch(self.root)["inspected_state_sha256"]
        self.assertNotEqual(first, second)
        self.write_cst(blocks=[block(pst_state(b"different"))])
        self.assertNotEqual(second, inspector.inspect_patch(self.root)["inspected_state_sha256"])

    def test_unsafe_plist_paths_are_rejected(self):
        for name in ("../escape.cst", "/tmp/escape.cst", "..\\escape.cst", "C:escape.cst", "bad\0name.cst"):
            self.metadata["channels"][0]["Filename"] = name
            self.write_metadata()
            with self.assertRaises(inspector.InspectionError):
                inspector.inspect_patch(self.root)

    def test_links_are_rejected_without_reading_their_targets(self):
        outside = Path(self.temporary.name) / "outside.cst"
        outside.write_bytes(cst())
        target = self.root / "#Root.cst"
        target.unlink()
        try:
            target.symlink_to(outside)
        except OSError:
            self.skipTest("Creating symlinks is not permitted on this host")
        with self.assertRaises(inspector.InspectionError):
            inspector.inspect_patch(self.root)

    def test_missing_required_members_are_errors(self):
        for name in ("data.plist", "#Root.cst"):
            data = (self.root / name).read_bytes()
            (self.root / name).unlink()
            with self.assertRaises(OSError):
                inspector.inspect_patch(self.root)
            (self.root / name).write_bytes(data)

    def test_selected_inner_files_resolve_to_their_existing_package(self):
        expected = inspector.inspect_patch(self.root)["inspected_state_sha256"]
        for member in ("data.plist", "#Root.cst"):
            report = inspector.inspect_patch(self.root / member)
            self.assertEqual(str(self.root), report["path"])
            self.assertEqual(expected, report["inspected_state_sha256"])
        with self.assertRaisesRegex(inspector.InspectionError, "does not exist"):
            inspector.inspect_patch(self.root / "missing.cst")

    def test_quoted_shell_escapes_and_trailing_space_have_actionable_errors(self):
        incorrect = str(self.root).replace(" ", "\\ ") + " "
        with self.assertRaises(inspector.InspectionError) as captured:
            inspector.inspect_patch(incorrect)
        self.assertIn("inside double quotes", str(captured.exception))
        self.assertIn("ends with a space", str(captured.exception))

    def test_standalone_preset_and_flat_patch_errors_explain_the_selected_type(self):
        preset = Path(self.temporary.name) / "standalone.pst"
        preset.write_bytes(b"test only")
        with self.assertRaisesRegex(inspector.InspectionError, "standalone .cst/.pst/.aupreset"):
            inspector.inspect_patch(preset)
        flat = Path(self.temporary.name) / "flat.patch"
        for data, explanation in ((b"PK\x03\x04test", "ZIP archive"), (b"bplist00test", "binary-plist file")):
            flat.write_bytes(data)
            with self.assertRaisesRegex(inspector.InspectionError, explanation):
                inspector.inspect_patch(flat)

    def test_invalid_plist_and_cyclic_or_expansive_graphs_are_bounded(self):
        for data in (b"not a plist", b"<?xml broken", plistlib.dumps([]),
                     b'<?xml version="1.0"?><!DOCTYPE plist [<!ENTITY x "test">]><plist><dict/></plist>'):
            with self.assertRaises(inspector.InspectionError):
                inspector._plist(data)
        cycle = {}
        cycle["cycle"] = cycle
        with self.assertRaises(inspector.InspectionError):
            inspector._json_value(cycle)
        expanded = [None] * 100001
        with self.assertRaises(inspector.InspectionError):
            inspector._json_value(expanded)

    def test_duplicate_plist_keys_cannot_hide_conflicting_routing(self):
        data = b'<?xml version="1.0"?><plist><dict><key>channels</key><array/><key>channels</key><array/></dict></plist>'
        with self.assertRaises(inspector.InspectionError):
            inspector._plist(data)

    def test_file_and_total_limits(self):
        with mock.patch.object(inspector, "MAX_FILE_BYTES", 10):
            with self.assertRaises(inspector.InspectionError):
                inspector.inspect_patch(self.root)
        with mock.patch.object(inspector, "MAX_MEMBERS", 1):
            with self.assertRaises(inspector.InspectionError):
                inspector.inspect_patch(self.root)
        with mock.patch.object(inspector, "MAX_PACKAGE_BYTES", (self.root / "data.plist").stat().st_size + 10):
            with self.assertRaises(inspector.InspectionError):
                inspector.inspect_patch(self.root)

    def test_invalid_container_lengths_do_not_create_a_successful_empty_chain(self):
        data = bytearray(cst([block(pst_state())]))
        struct.pack_into("<I", data, 237 + 28, 0xFFFFFFFF)
        (self.root / "#Root.cst").write_bytes(data)
        channel = inspector.inspect_patch(self.root)["channels"][0]
        self.assertEqual([], channel["plugins"])
        self.assertIn("Unreadable channel strip", channel["warnings"][0])
        self.assertFalse(inspector.inspect_patch(self.root)["rendering_supported"])

    def test_truncated_state_lengths_and_unrecognized_tail_are_explicit(self):
        bad_pst = bytearray(pst_state())
        struct.pack_into("<I", bad_pst, 0, 0xFFFFFFFF)
        with self.assertRaises(inspector.InspectionError):
            inspector.inspect_cst(cst([block(bad_pst)]))
        bad_binary = bytearray(block(plistlib.dumps(au_state(), fmt=plistlib.FMT_BINARY), direct=True))
        struct.pack_into("<I", bad_binary, 52, 0xFFFFFFFF)
        with self.assertRaises(inspector.InspectionError):
            inspector.inspect_cst(cst([bad_binary]))
        report = inspector.inspect_cst(cst() + b"unknown suffix")
        self.assertTrue(any("container tail" in warning for warning in report["warnings"]))

    def test_unknown_au_role_and_identity_are_not_guessed(self):
        value = au_state("xxxx")
        del value["manufacturer"]
        self.write_cst(blocks=[block(plistlib.dumps(value, fmt=plistlib.FMT_BINARY), direct=True)])
        plugin = inspector.inspect_patch(self.root)["channels"][0]["plugins"][0]
        self.assertEqual("unknown", plugin["role"])
        self.assertIsNone(plugin["component"])

    def test_metadata_duplicates_wrong_send_type_and_missing_root_flag(self):
        self.metadata["channels"].append(dict(self.metadata["channels"][0]))
        self.write_metadata()
        with self.assertRaises(inspector.InspectionError):
            inspector.inspect_patch(self.root)
        self.metadata["channels"].pop()
        self.metadata["channels"][0]["Channel_sends"] = "invalid"
        self.write_metadata()
        with self.assertRaises(inspector.InspectionError):
            inspector.inspect_patch(self.root)
        del self.metadata["channels"][0]["Channel_sends"]
        del self.metadata["channels"][0]["Root"]
        self.write_metadata()
        self.assertTrue(any("Root channel metadata" in warning for warning in inspector.inspect_patch(self.root)["warnings"]))

    def test_cli_json_and_summary_and_invalid_input_exit_status(self):
        for args in (["--json"], []):
            result = subprocess.run([sys.executable, str(SCRIPT), str(self.root), *args],
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            if args:
                self.assertFalse(json.loads(result.stdout)["rendering_supported"])
            else:
                self.assertIn("Slot 2 instrument", result.stdout)
                self.assertIn("not implemented", result.stdout)
        result = subprocess.run([sys.executable, str(SCRIPT), str(self.root / "missing.patch")],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(1, result.returncode)
        self.assertIn("Inspection failed", result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_installed_check_only_enumerates_and_never_validates_plugins(self):
        completed = subprocess.CompletedProcess([], 0, "aumu Test TEST  -  TEST ONLY vendor\n", "")
        output = io.StringIO()
        with mock.patch.object(inspector.sys, "platform", "darwin"), mock.patch.object(inspector.subprocess, "run", return_value=completed) as run, redirect_stdout(output):
            self.assertEqual(0, inspector.main([str(self.root), "--json", "--check-installed"]))
        run.assert_called_once_with(["/usr/bin/auval", "-a"], check=True, capture_output=True, text=True, timeout=30)
        self.assertEqual("registered", json.loads(output.getvalue())["channels"][0]["plugins"][0]["availability"])

    def test_auval_failure_is_not_reported_as_missing_plugins(self):
        with mock.patch.object(inspector.sys, "platform", "darwin"), mock.patch.object(inspector.subprocess, "run", side_effect=subprocess.TimeoutExpired("auval", 30)), redirect_stderr(io.StringIO()):
            self.assertEqual(1, inspector.main([str(self.root), "--check-installed"]))


if __name__ == "__main__":
    unittest.main()
