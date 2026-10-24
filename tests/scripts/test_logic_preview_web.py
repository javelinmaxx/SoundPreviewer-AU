"""Functional HTTP requests with a synthetic renderer; no browser or AU required."""
import http.client
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
import wave

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import logic_preview_web as web


class LogicPreviewWebTests(unittest.TestCase):
    def setUp(self):
        quiet = mock.patch.object(web.Handler, "log_message", lambda *args: None)
        quiet.start()
        self.addCleanup(quiet.stop)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.source = self.base / "library"
        (self.source / "Group/One.patch").mkdir(parents=True)
        (self.source / "Group/Two.patch").mkdir()
        self.output = self.base / "output"
        self.worker = self.base / "worker"
        self.worker.write_text("TEST ONLY placeholder")
        self.worker.chmod(0o755)
        def renderer(patch, output, worker, **settings):
            output.parent.mkdir(parents=True, exist_ok=True)
            with wave.open(str(output), "wb") as audio:
                audio.setparams((2, 2, 44100, 0, "NONE", "not compressed"))
                audio.writeframes(b"\x01\x00\x02\x00" * 441)
            return {"output": str(output), "approximations": []}
        self.platform = mock.patch.object(web.sys, "platform", "darwin")
        self.platform.start()
        self.addCleanup(self.platform.stop)
        self.jobs = web.Jobs(self.worker, renderer)
        self.server = web.Server(0, self.jobs)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.cleanup_server)
        self.host = f"localhost:{self.server.server_address[1]}"

    def cleanup_server(self):
        self.jobs.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)

    def request(self, path, payload=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_address[1], timeout=10)
        extra = {"Host": self.host}
        if payload is not None:
            extra.update({"Content-Type": "application/json", "X-Preview-Token": self.server.token})
        extra.update(headers or {})
        connection.request("POST" if payload is not None else "GET", path,
                           json.dumps(payload) if payload is not None else None, extra)
        response = connection.getresponse()
        result = response.status, dict(response.getheaders()), response.read()
        connection.close()
        return result

    def start_job(self):
        status, _, data = self.request("/api/jobs", {"source": str(self.source), "output": str(self.output)})
        self.assertEqual(202, status, data)
        return json.loads(data)["id"]

    def wait_job(self, identifier):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            status, _, raw = self.request("/api/jobs/" + identifier)
            self.assertEqual(200, status)
            result = json.loads(raw)
            if result["status"] not in ("scanning", "rendering", "cancelling"):
                return result
            time.sleep(0.01)
        self.fail("Synthetic job did not finish")

    def test_serves_ui_and_assets_with_same_origin_controls(self):
        status, headers, page = self.request("/")
        self.assertEqual(200, status)
        self.assertIn(b"Logic Preview", page)
        self.assertNotIn(b"__PREVIEW_TOKEN__", page)
        self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
        for path in ("/app.js", "/style.css", "/api/status"):
            self.assertEqual(200, self.request(path)[0])
        self.assertEqual(403, self.request("/api/status", headers={"Host": "attacker.invalid:5002"})[0])
        self.assertEqual(403, self.request("/api/jobs", {}, {"Origin": "https://outside.invalid"})[0])
        self.assertEqual(403, self.request("/api/jobs", {}, {"X-Preview-Token": "wrong"})[0])
        self.assertEqual(404, self.request("/../../Program.cs")[0])

    def test_renders_recursively_saves_report_and_serves_seekable_audio(self):
        identifier = self.start_job()
        job = self.wait_job(identifier)
        self.assertEqual("completed", job["status"], job)
        self.assertEqual(2, job["rendered"])
        self.assertTrue(Path(job["report_path"]).is_file())
        status, headers, data = self.request(f"/api/audio/{identifier}/0", headers={"Range": "bytes=0-43"})
        self.assertEqual(206, status)
        self.assertEqual(44, len(data))
        self.assertEqual(b"RIFF", data[:4])
        self.assertEqual("bytes", headers["Accept-Ranges"])
        self.assertEqual(416, self.request(f"/api/audio/{identifier}/0", headers={"Range": "bytes=999999-"})[0])
        after = json.loads(self.request(f"/api/jobs/{identifier}?after=1")[2])
        self.assertEqual(1, len(after["results"]))
        repeated = self.wait_job(self.start_job())
        self.assertEqual(2, repeated["skipped_existing"])

    def test_cancellation_stops_active_work_and_rejects_a_second_job(self):
        started = threading.Event()
        def renderer(source, output, worker, **settings):
            started.set()
            settings["cancel_event"].wait(3)
            raise KeyboardInterrupt()
        self.jobs.render_one = renderer
        identifier = self.start_job()
        self.assertTrue(started.wait(2))
        self.assertEqual(409, self.request("/api/jobs", {"source": str(self.source)})[0])
        self.assertEqual(200, self.request(f"/api/jobs/{identifier}/cancel", {})[0])
        job = self.wait_job(identifier)
        self.assertEqual("cancelled", job["status"])
        self.assertEqual(1, job["unrun"])
        self.assertFalse(list(self.output.rglob("*.wav")))

    def test_audio_is_restricted_to_real_outputs_and_invalid_settings_fail(self):
        self.assertEqual(400, self.request("/api/jobs", {"source": str(self.source), "hold": -1})[0])
        identifier = self.start_job()
        self.wait_job(identifier)
        self.assertEqual(404, self.request(f"/api/audio/{identifier}/99")[0])
        path = self.output / "Group/One.wav"
        path.write_bytes(b"not a WAV")
        self.assertEqual(400, self.request(f"/api/audio/{identifier}/0")[0])


if __name__ == "__main__":
    unittest.main()
