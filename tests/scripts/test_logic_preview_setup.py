"""Exercise shell setup/launch contracts using compiler stubs, not Apple frameworks."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipIf(os.name == "nt", "Mac setup shell tests require POSIX")
class LogicPreviewSetupTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "repo with spaces"
        (self.root / "scripts").mkdir(parents=True)
        (self.root / "src/SoundPreviewer.LogicPreview").mkdir(parents=True)
        self.source = self.root / "src/SoundPreviewer.LogicPreview/main.swift"
        self.source.write_text("// TEST ONLY compiler input\n")
        project = Path(__file__).resolve().parents[2]
        shutil.copy2(project / "scripts/Setup-LogicPreview.sh", self.root / "scripts/Setup-LogicPreview.sh")
        shutil.copy2(project / "Preview-LogicPatch.command", self.root / "Preview-LogicPatch.command")
        tools = self.root / "test tools"
        tools.mkdir()
        self.log = self.root / "compiler.jsonl"
        self.environment = os.environ.copy()
        self.environment.update(PATH=str(tools) + os.pathsep + self.environment["PATH"],
                                SOUNDPREVIEWER_PYTHON=sys.executable, LOGIC_SETUP_LOG=str(self.log))
        self.executable(tools / "uname", "import os,sys\nprint('Darwin' if sys.argv[1]=='-s' else os.environ.get('LOGIC_SETUP_ARCH','arm64'))\n")
        self.compiler = tools / "swift compiler"
        self.executable(self.compiler, '''import json,os,pathlib,sys
if '--version' in sys.argv:
 print('TEST ONLY Swift compiler');sys.exit(0)
with open(os.environ['LOGIC_SETUP_LOG'],'a')as log:log.write(json.dumps(sys.argv[1:])+'\\n')
if os.environ.get('LOGIC_SETUP_FAIL')=='1':sys.exit(19)
out=pathlib.Path(sys.argv[sys.argv.index('-o')+1])
out.write_text('#!/bin/bash\\necho "TEST ONLY self-test stub"\\n')
out.chmod(0o755)
''')
        self.executable(tools / "xcrun", f"import sys\nprint({str(self.compiler)!r} if '--find' in sys.argv else {str(self.root / 'fake SDK')!r})\n")

    def executable(self, path, code):
        path.write_text(f"#!{sys.executable}\n" + code, encoding="utf-8")
        path.chmod(0o755)

    def setup(self, *arguments):
        return subprocess.run(["/bin/bash", str(self.root / "scripts/Setup-LogicPreview.sh"), *arguments],
                              env=self.environment, capture_output=True, text=True, timeout=20)

    def compilations(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def test_repeated_setup_reuses_build_and_self_test_is_explicit(self):
        result = self.setup("--self-test")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("TEST ONLY self-test stub", result.stdout)
        result = self.setup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("current", result.stderr)
        self.assertEqual(1, len(self.compilations()))
        self.assertIn("arm64-apple-macosx13.0", self.compilations()[0])

    def test_source_architecture_and_force_refresh_build(self):
        self.assertEqual(0, self.setup().returncode)
        self.source.write_text("// changed TEST ONLY input")
        self.assertEqual(0, self.setup().returncode)
        self.environment["LOGIC_SETUP_ARCH"] = "x86_64"
        self.assertEqual(0, self.setup().returncode)
        self.assertEqual(0, self.setup("--force").returncode)
        self.assertEqual(4, len(self.compilations()))
        self.assertIn("x86_64-apple-macosx13.0", self.compilations()[-1])

    def test_failed_rebuild_preserves_previous_binary_and_does_not_update_fingerprint(self):
        self.assertEqual(0, self.setup().returncode)
        directory = self.root / ".build/logic-preview"
        original = (directory / "build.sha256").read_bytes()
        binary = (directory / "LogicPreview").read_bytes()
        self.source.write_text("// changed TEST ONLY input")
        self.environment["LOGIC_SETUP_FAIL"] = "1"
        self.assertEqual(19, self.setup().returncode)
        self.assertEqual(original, (directory / "build.sha256").read_bytes())
        self.assertEqual(binary, (directory / "LogicPreview").read_bytes())
        self.assertEqual([], list(directory.glob("compile.*")))

    def test_launcher_forwards_spaces_and_lets_user_disable_playback(self):
        (self.root / "scripts/logic_patch_preview.py").write_text("import json,sys\nprint(json.dumps(sys.argv[1:]));sys.exit(7)\n")
        result = subprocess.run(["/bin/bash", str(self.root / "Preview-LogicPatch.command"), "a sound.patch", "--no-play"],
                                env=self.environment, capture_output=True, text=True, timeout=20)
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertEqual(["--play", "a sound.patch", "--no-play"], json.loads(result.stdout))

    def test_relative_patch_paths_are_resolved_from_the_callers_directory(self):
        caller = Path(self.temporary.name) / "another folder"
        (caller / "Sounds/Deluxe Classic.patch").mkdir(parents=True)
        (self.root / "scripts/logic_patch_preview.py").write_text(
            "import json,sys\nfrom pathlib import Path\nprint(json.dumps(str(Path(sys.argv[2]).resolve())))\n")
        result = subprocess.run(["/bin/bash", str(self.root / "Preview-LogicPatch.command"), "Sounds/Deluxe Classic.patch"],
                                cwd=caller, env=self.environment, capture_output=True, text=True, timeout=20)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str((caller / "Sounds/Deluxe Classic.patch").resolve()), json.loads(result.stdout))


if __name__ == "__main__":
    unittest.main()
