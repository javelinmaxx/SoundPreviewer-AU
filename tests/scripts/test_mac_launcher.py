"""Run the source launcher against an SDK stub without opening audio or a GUI."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipIf(os.name == "nt", "The macOS source launcher requires a POSIX shell")
class MacLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "checkout with spaces"
        self.root.mkdir()
        source = Path(__file__).resolve().parents[2] / "Start-SoundPreviewer.command"
        shutil.copy2(source, self.root / source.name)
        self.sdk = Path(self.temporary.name) / "sdk with spaces"
        self.sdk.mkdir()
        dotnet = self.sdk / "dotnet"
        dotnet.write_text(
            f"#!{sys.executable}\n"
            "import json, os, sys\n"
            "with open(os.environ['LAUNCH_TEST_LOG'], 'a') as log:\n"
            "    log.write(json.dumps({'args': sys.argv[1:], 'cwd': os.getcwd(),\n"
            "        'cli': os.environ['DOTNET_CLI_HOME'], 'packages': os.environ['NUGET_PACKAGES']}) + '\\n')\n"
            "if sys.argv[1] == 'build':\n"
            "    sys.exit(int(os.environ.get('LAUNCH_TEST_BUILD_EXIT', '0')))\n"
            "sys.exit(int(os.environ.get('LAUNCH_TEST_APP_EXIT', '0')))\n",
            encoding="utf-8",
        )
        dotnet.chmod(0o755)
        self.log = Path(self.temporary.name) / "calls.jsonl"
        self.environment = os.environ.copy()
        for name in ("DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES"):
            self.environment.pop(name, None)
        self.environment.update(DOTNET_ROOT=str(self.sdk), LAUNCH_TEST_LOG=str(self.log))

    def launch(self, *arguments):
        result = subprocess.run(
            ["/bin/bash", str(self.root / "Start-SoundPreviewer.command"), *arguments],
            cwd=self.temporary.name, env=self.environment, capture_output=True, text=True,
            timeout=20,
        )
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def test_builds_only_app_then_forwards_arguments_and_exit_status(self):
        self.environment["LAUNCH_TEST_APP_EXIT"] = "7"
        result, calls = self.launch("--data-directory", "a library with spaces")
        self.assertEqual(7, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))
        self.assertEqual(
            ["build", "src/SoundPreviewer.App/SoundPreviewer.App.csproj", "-c", "Release",
             f"-p:RestoreConfigFile={self.root}/NuGet.Config"], calls[0]["args"])
        self.assertEqual(self.root.resolve(), Path(calls[0]["cwd"]).resolve())
        self.assertEqual([str(self.root / "src/SoundPreviewer.App/bin/Release/net8.0/SoundPreviewer.dll"),
                          "--data-directory", "a library with spaces"], calls[1]["args"])
        self.assertEqual(str(self.root / ".build/nuget-packages"), calls[0]["packages"])

    def test_preserves_explicit_caches(self):
        self.environment.update(DOTNET_CLI_HOME=str(self.root / "custom cli"),
                                NUGET_PACKAGES=str(self.root / "custom packages"))
        result, calls = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.environment["DOTNET_CLI_HOME"], calls[0]["cli"])
        self.assertEqual(self.environment["NUGET_PACKAGES"], calls[0]["packages"])

    def test_reuses_legacy_launch_cache(self):
        (self.root / ".build/nuget").mkdir(parents=True)
        result, calls = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str(self.root / ".build/nuget"), calls[0]["packages"])

    def test_prefers_existing_shared_publish_cache(self):
        for directory in ("nuget", "nuget-packages"):
            (self.root / ".build" / directory).mkdir(parents=True, exist_ok=True)
        result, calls = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str(self.root / ".build/nuget-packages"), calls[0]["packages"])

    def test_build_failure_does_not_launch_stale_output(self):
        self.environment["LAUNCH_TEST_BUILD_EXIT"] = "42"
        result, calls = self.launch()
        self.assertEqual(42, result.returncode, result.stderr)
        self.assertEqual(1, len(calls))

    def test_discovers_sdk_on_path(self):
        self.environment.pop("DOTNET_ROOT")
        self.environment["PATH"] = str(self.sdk) + os.pathsep + self.environment["PATH"]
        result, calls = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, len(calls))


if __name__ == "__main__":
    unittest.main()
