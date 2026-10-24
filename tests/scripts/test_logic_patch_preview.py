"""Planner and process/file contracts; synthetic workers aren't native AU tests."""
import json
import os
from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

from test_logic_patch_inspector import au_state, block, cst, pst_state

SCRIPTS = Path(__file__).resolve().parents[2] / "scripts"
sys.path.insert(0, str(SCRIPTS))
import logic_patch_preview as preview

class LogicPatchPreviewTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "TEST ONLY chain.patch"
        self.root.mkdir()
        self.metadata = {"VersionPatches": 40014, "channels": [{"Filename": "#Root.cst", "Root": True,
            "Channel_inputIndex_1": -1, "Channel_inputIsBus": False, "Channel_outputIsBus": False,
            "Channel_outputIsStereo": True, "Channel_outputIndex": 0, "Channel_sends": [{}, {}]}]}
        self.write([block(plistlib.dumps(au_state()))])
        self.output = Path(self.temporary.name) / "preview output.wav"

    def write(self, blocks):
        (self.root / "data.plist").write_bytes(plistlib.dumps(self.metadata))
        (self.root / "#Root.cst").write_bytes(cst(blocks))

    def report(self):
        return preview.inspector.inspect_patch(self.root)

    def test_plans_one_instrument_and_ordered_effects(self):
        self.write([block(plistlib.dumps(au_state())),
                    block(plistlib.dumps(au_state("aufx", "Fx01")), slot=3, role=0),
                    block(plistlib.dumps(au_state("aufx", "Fx02")), slot=4, role=0)])
        plan = preview.plan_patch(self.report())
        self.assertEqual("Test", plan["instrument"]["component"]["subtype"])
        self.assertEqual(["Fx01", "Fx02"], [p["component"]["subtype"] for p in plan["effects"]])
        self.assertFalse(plan["logic_equivalence_verified"])

    def test_metadata_approximation_requires_explicit_opt_in(self):
        archive = plistlib.dumps({"$archiver": "NSKeyedArchiver"}, fmt=plistlib.FMT_BINARY)
        self.write([block(plistlib.dumps(au_state())), block(archive, slot=9, direct=True)])
        with self.assertRaisesRegex(preview.PreviewError, "au-chain-only"):
            preview.plan_patch(self.report())
        self.assertTrue(preview.plan_patch(self.report(), True)["approximations"])

    def test_never_silently_omits_native_pst_or_midi_effects(self):
        for extra in (block(pst_state(), slot=3, role=0), block(plistlib.dumps(au_state("aumi")), slot=7, role=2)):
            self.write([block(plistlib.dumps(au_state())), extra])
            for allow in (False, True):
                with self.assertRaises(preview.PreviewError):
                    preview.plan_patch(self.report(), allow)

    def test_bus_sends_mute_and_stacks_are_rejected_even_in_chain_mode(self):
        setting = self.metadata["channels"][0]
        for key, value in (("Channel_outputIsBus", True), ("Channel_outputIndex", 2),
                           ("Channel_sends", [{"destination": 1}]), ("Channel_isMuted", True)):
            original = setting.get(key)
            setting[key] = value
            self.write([block(plistlib.dumps(au_state()))])
            with self.assertRaises(preview.PreviewError):
                preview.plan_patch(self.report(), True)
            if original is None:
                setting.pop(key)
            else:
                setting[key] = original
        self.write([block(plistlib.dumps(au_state()))])
        report = self.report()
        report["channels"].append(dict(report["channels"][0]))
        with self.assertRaises(preview.PreviewError):
            preview.plan_patch(report, True)

    def test_unknown_mix_controls_are_explicit_approximations(self):
        self.metadata["channels"][0]["Channel_pan"] = 0.7
        self.write([block(plistlib.dumps(au_state()))])
        with self.assertRaises(preview.PreviewError):
            preview.plan_patch(self.report())
        self.assertIn("Channel_pan", " ".join(preview.plan_patch(self.report(), True)["approximations"]))

    def fake_worker(self):
        path = Path(self.temporary.name) / "fake worker"
        path.write_text(f"#!{sys.executable}\n" + '''
import hashlib,json,math,os,pathlib,struct,sys,time,wave
request_path=pathlib.Path(sys.argv[2]); root=request_path.parent
r=json.loads(request_path.read_text())
for p in [r['instrument']]+r['effects']:
 assert hashlib.sha256((root/p['stateFile']).read_bytes()).hexdigest()==p['stateSHA256']
mode=os.environ.get('LOGIC_TEST_MODE','normal')
if mode=='fail': sys.exit(17)
if mode=='timeout': time.sleep(30)
segment=math.floor(r['holdSeconds']*r['sampleRate']+0.5)+math.floor(r['tailSeconds']*r['sampleRate']+0.5)
frames=segment*len(r['notes'])
with wave.open(str(root/'preview.wav'),'wb') as w:
 w.setparams((2,2,r['sampleRate'],0,'NONE','not compressed'))
 data=b''.join(struct.pack('<hh',*[0 if mode=='silent' else int(4000*math.sin(i*.1))]*2) for i in range(frames))
 w.writeframes(data)
result={'schemaVersion':1,'success':True,'frames':frames,'sampleRate':r['sampleRate'],'channels':2,
 'notes':[{'pitch':p,'startFrame':i*segment,'frames':segment}for i,p in enumerate(r['notes'])]}
if mode=='wrong_notes': result['notes'][0]['pitch']=127
(root/'result.json').write_text(json.dumps(result))
''', encoding="utf-8")
        path.chmod(0o755)
        return path

    @unittest.skipIf(os.name == "nt", "Native worker process-group contract requires POSIX")
    def test_process_contract_stages_exact_state_validates_audio_and_preserves_source(self):
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        with mock.patch.object(preview.sys, "platform", "darwin"):
            report = preview.render_preview(self.root, self.output, self.fake_worker(), hold=0.05, tail=0)
        self.assertEqual(before, {p.name: p.read_bytes() for p in self.root.iterdir()})
        self.assertTrue(self.output.is_file())
        self.assertEqual([60, 84, 36], report["notes"])
        self.assertEqual(6615, report["metrics"]["frames"])
        self.assertFalse(report["logic_equivalence_verified"])

    @unittest.skipIf(os.name == "nt", "POSIX executable fixture")
    def test_failure_silence_wrong_note_and_timeout_never_publish_audio(self):
        worker = self.fake_worker()
        for mode in ("fail", "silent", "wrong_notes", "timeout"):
            with mock.patch.object(preview.sys, "platform", "darwin"), mock.patch.dict(os.environ, LOGIC_TEST_MODE=mode):
                with self.assertRaises((preview.PreviewError, subprocess.TimeoutExpired)):
                    preview.render_preview(self.root, self.output, worker, hold=0.05, tail=0, timeout=1)
            self.assertFalse(self.output.exists())

    @unittest.skipIf(os.name == "nt", "POSIX executable fixture")
    def test_existing_output_and_filename_races_preserve_old_audio(self):
        worker = self.fake_worker()
        self.output.write_bytes(b"old audio")
        with mock.patch.object(preview.sys, "platform", "darwin"), self.assertRaises(preview.PreviewError):
            preview.render_preview(self.root, self.output, worker)
        self.assertEqual(b"old audio", self.output.read_bytes())
        self.output.unlink()
        def win_race(source, target):
            Path(target).write_bytes(b"someone else's audio")
            raise FileExistsError("TEST ONLY competing writer")
        with mock.patch.object(preview.sys, "platform", "darwin"), mock.patch.object(preview.os, "link", side_effect=win_race):
            with self.assertRaises(FileExistsError):
                preview.render_preview(self.root, self.output, worker, hold=0.05, tail=0)
        self.assertEqual(b"someone else's audio", self.output.read_bytes())
        self.assertEqual([], list(self.output.parent.glob(".logic-preview-*")))

    def test_output_inside_patch_and_invalid_timing_are_rejected(self):
        with mock.patch.object(preview.sys, "platform", "darwin"):
            with self.assertRaises(preview.PreviewError):
                preview.render_preview(self.root, self.root / "preview.wav", Path("unused"))
            for timing in (float("nan"), float("inf"), -1):
                with self.assertRaises(preview.PreviewError):
                    preview.render_preview(self.root, self.output, Path("unused"), hold=timing)

    @unittest.skipIf(os.name == "nt", "POSIX worker cancellation")
    def test_cancellation_event_terminates_worker_without_publishing(self):
        cancel = threading.Event()
        timer = threading.Timer(0.1, cancel.set)
        timer.start()
        try:
            with mock.patch.object(preview.sys, "platform", "darwin"), mock.patch.dict(os.environ, LOGIC_TEST_MODE="timeout"):
                with self.assertRaises(KeyboardInterrupt):
                    preview.render_preview(self.root, self.output, self.fake_worker(), cancel_event=cancel)
            self.assertFalse(self.output.exists())
        finally:
            timer.cancel()


if __name__ == "__main__":
    unittest.main()
