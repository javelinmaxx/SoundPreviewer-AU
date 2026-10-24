#!/usr/bin/env python3
"""Loopback-only web UI for saved Logic AU-chain previews, default port 5002."""
from __future__ import annotations

import argparse
import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import threading
import urllib.parse
import uuid
import wave
import webbrowser

import logic_patch_batch as batch
import logic_patch_preview as preview

STATIC = Path(__file__).resolve().parent.parent / "web/logic-preview"


class BusyError(ValueError):
    pass


class Jobs:
    def __init__(self, worker=preview.DEFAULT_WORKER, render_one=preview.render_preview):
        self.worker = Path(worker)
        self.render_one = render_one
        self.lock = threading.Lock()
        self.jobs = {}
        self.active = None

    @property
    def native_ready(self):
        return sys.platform == "darwin" and self.worker.is_file() and os.access(self.worker, os.X_OK)

    def start(self, payload):
        if not self.native_ready:
            raise ValueError("Rendering requires macOS and the native worker. Start this app with Start-LogicPreview-Web.command on your Mac.")
        source = payload.get("source")
        output = payload.get("output")
        if not isinstance(source, str) or not source.strip():
            raise ValueError("Choose a .patch package or a folder containing patches.")
        if output is not None and not isinstance(output, str):
            raise ValueError("The output folder must be a path.")
        source = Path(source).expanduser().absolute()
        if not source.exists():
            raise ValueError(f"Source does not exist: {str(source)!r}. Paste paths without shell quotes or backslash escapes.")
        output = Path(output).expanduser().absolute() if output else preview.PROJECT / "artifacts/logic-preview/web" / (source.stem or "library")
        try:
            notes = tuple(int(value) for value in str(payload.get("notes", "60,84,36")).split(","))
            options = dict(notes=notes, hold=float(payload.get("hold", 0.5)), tail=float(payload.get("tail", 0.35)),
                           velocity=int(payload.get("velocity", 89)), sample_rate=int(payload.get("sample_rate", 44100)),
                           bpm=float(payload.get("bpm", 120)), timeout=float(payload.get("timeout", 120)))
            preview.validate_options(**options)
        except (TypeError, ValueError) as error:
            raise ValueError(f"Invalid render settings: {error}") from error
        if type(payload.get("au_chain_only", False)) is not bool:
            raise ValueError("AU-chain-only must be a checkbox value.")
        options["au_chain_only"] = payload.get("au_chain_only", False)
        with self.lock:
            if self.active and self.jobs[self.active]["status"] in ("scanning", "rendering", "cancelling"):
                raise BusyError("A render is already running. Wait for it or stop it first.")
            identifier = uuid.uuid4().hex
            job = {"id": identifier, "status": "scanning", "source": str(source), "output": str(output),
                   "total": None, "processed": 0, "rendered": 0, "failed": 0, "skipped_existing": 0,
                   "current": "", "results": [], "discovery_issues": [], "message": "Scanning for patches…",
                   "cancel": threading.Event(), "thread": None}
            self.jobs[identifier] = job
            self.active = identifier
            thread = threading.Thread(target=self._run, args=(job, source, output, options), daemon=True)
            job["thread"] = thread
            thread.start()
        return identifier

    def _run(self, job, source, output, options):
        def discovered(total, issues):
            with self.lock:
                job.update(total=total, discovery_issues=issues, status="cancelling" if job["cancel"].is_set() else "rendering")

        def render_one(patch, destination, worker, **settings):
            with self.lock:
                job.update(current=patch.name, message="Rendering " + patch.name)
            return self.render_one(patch, destination, worker, **settings)

        def progress(index, total, entry):
            with self.lock:
                job.update(processed=index, total=total, current=entry["relative_source"])
                job["results"].append(entry)
                key = {"rendered": "rendered", "failed": "failed", "skipped_existing": "skipped_existing"}.get(entry["status"])
                if key:
                    job[key] += 1

        try:
            report = batch.render_batch(source, output, self.worker, render_one=render_one, progress=progress,
                                        on_discovered=discovered, cancel_event=job["cancel"], **options)
            with self.lock:
                status = "cancelled" if report["cancelled"] else "completed_with_errors" if report["failed"] or report["discovery_failed"] else "completed"
                job.update(status=status, report_path=report["report_path"], unrun=report["unrun"],
                           message="Stopped; completed WAVs are saved." if report["cancelled"] else "Finished. The batch report is saved.")
        except Exception as error:
            with self.lock:
                job.update(status="failed", message=str(error))

    def snapshot(self, identifier, after=0):
        with self.lock:
            job = self.jobs.get(identifier)
            if job is None:
                raise KeyError(identifier)
            data = {key: value for key, value in job.items() if key not in ("thread", "cancel", "results")}
            data["results"] = job["results"][after:]
            data["result_count"] = len(job["results"])
            return copy.deepcopy(data)

    def cancel(self, identifier):
        with self.lock:
            job = self.jobs.get(identifier)
            if job is None:
                raise KeyError(identifier)
            if job["status"] in ("scanning", "rendering", "cancelling"):
                job["cancel"].set()
                job.update(status="cancelling", message="Stopping the current render…")

    def audio_path(self, identifier, index):
        with self.lock:
            job = self.jobs.get(identifier)
            if job is None or not 0 <= index < len(job["results"]):
                raise KeyError(identifier)
            entry = job["results"][index]
            if entry["status"] not in ("rendered", "skipped_existing"):
                raise ValueError("This result has no saved audio.")
            path = Path(entry["output"])
            root = Path(job["output"]).resolve()
        if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(root):
            raise ValueError("Saved audio is no longer available in the output folder.")
        if path.stat().st_size > 64 * 1024 * 1024:
            raise ValueError("Audio exceeds the playback size limit.")
        with wave.open(str(path), "rb") as audio:
            if audio.getcomptype() != "NONE":
                raise ValueError("Saved output is not an uncompressed WAV.")
        return path

    def close(self):
        with self.lock:
            jobs = list(self.jobs.values())
            for job in jobs:
                job["cancel"].set()
        for job in jobs:
            job["thread"].join(timeout=3)


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, port=5002, jobs=None):
        self.jobs = jobs or Jobs()
        self.token = secrets.token_hex(32)
        self.browse_lock = threading.Lock()
        super().__init__(("127.0.0.1", port), Handler)
        port = self.server_address[1]
        self.hosts = {f"localhost:{port}", f"127.0.0.1:{port}"}
        self.origins = {"http://" + host for host in self.hosts}

    def get_request(self):
        connection, address = super().get_request()
        connection.settimeout(15)
        return connection, address


class Handler(BaseHTTPRequestHandler):
    def _allowed(self, write=False):
        if self.headers.get("Host") not in self.server.hosts or self.headers.get("Origin") not in (None, *self.server.origins):
            self._json(403, {"error": "Only this app's localhost origin is allowed."})
            return False
        if write and not secrets.compare_digest(self.headers.get("X-Preview-Token", ""), self.server.token):
            self._json(403, {"error": "Reload this page before starting another action."})
            return False
        return True

    def _headers(self, status, kind, length):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(length))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; media-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'")

    def _json(self, status, data):
        raw = json.dumps(data, ensure_ascii=False, allow_nan=False).encode("utf-8")
        self._headers(status, "application/json; charset=utf-8", len(raw))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if not self._allowed():
            return
        url = urllib.parse.urlsplit(self.path)
        path = url.path
        try:
            if path == "/favicon.ico":
                self._headers(204, "image/x-icon", 0)
                self.end_headers()
                return
            if path == "/api/status":
                return self._json(200, {"native_ready": self.server.jobs.native_ready, "active_job": self.server.jobs.active,
                                        "default_output": str(preview.PROJECT / "artifacts/logic-preview/web")})
            match = re.fullmatch(r"/api/jobs/([a-f0-9]{32})", path)
            if match:
                after = int(urllib.parse.parse_qs(url.query).get("after", ["0"])[0])
                if after < 0:
                    raise ValueError("Invalid result cursor.")
                return self._json(200, self.server.jobs.snapshot(match[1], after))
            match = re.fullmatch(r"/api/audio/([a-f0-9]{32})/(\d+)", path)
            if match:
                return self._audio(self.server.jobs.audio_path(match[1], int(match[2])))
            files = {"/": ("index.html", "text/html; charset=utf-8"),
                     "/app.js": ("app.js", "text/javascript; charset=utf-8"),
                     "/style.css": ("style.css", "text/css; charset=utf-8")}
            if path not in files:
                return self._json(404, {"error": "Not found."})
            name, kind = files[path]
            data = (STATIC / name).read_bytes()
            if name == "index.html":
                data = data.replace(b"__PREVIEW_TOKEN__", self.server.token.encode("ascii"))
            self._headers(200, kind, len(data))
            self.end_headers()
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            return
        except KeyError:
            self._json(404, {"error": "Job or audio not found."})
        except (ValueError, OSError, EOFError, wave.Error) as error:
            self._json(400, {"error": str(error)})

    def do_POST(self):
        if not self._allowed(write=True):
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 16384 or self.headers.get("Content-Type", "").split(";")[0] != "application/json":
                raise ValueError("Expected a JSON request up to 16 KiB.")
            payload = json.loads(self.rfile.read(length))
            if not isinstance(payload, dict):
                raise ValueError("Expected a JSON object.")
            if self.path == "/api/jobs":
                return self._json(202, {"id": self.server.jobs.start(payload)})
            match = re.fullmatch(r"/api/jobs/([a-f0-9]{32})/cancel", self.path)
            if match:
                self.server.jobs.cancel(match[1])
                return self._json(200, {"ok": True})
            if self.path == "/api/browse":
                if sys.platform != "darwin":
                    raise ValueError("The native picker requires macOS. Paste the source path instead.")
                kind = payload.get("kind")
                if kind not in ("folder", "patch"):
                    raise ValueError("Choose a folder or patch.")
                if not self.server.browse_lock.acquire(blocking=False):
                    raise BusyError("A picker is already open.")
                try:
                    script = 'POSIX path of (choose folder with prompt "Choose a Logic patch library")' if kind == "folder" else 'POSIX path of (choose file with prompt "Choose a Logic .patch package")'
                    result = subprocess.run(["/usr/bin/osascript", "-e", script], capture_output=True, text=True, timeout=180)
                    if result.returncode:
                        if "-128" in result.stderr:
                            return self._json(200, {"cancelled": True})
                        raise ValueError("Could not open the picker. Paste the path instead.")
                    return self._json(200, {"path": result.stdout.rstrip("\r\n")})
                finally:
                    self.server.browse_lock.release()
            return self._json(404, {"error": "Not found."})
        except (BrokenPipeError, ConnectionResetError):
            return
        except BusyError as error:
            self._json(409, {"error": str(error)})
        except KeyError:
            self._json(404, {"error": "Job not found."})
        except (ValueError, OSError, subprocess.SubprocessError) as error:
            self._json(400, {"error": str(error)})

    def _audio(self, path):
        with path.open("rb") as source:
            size = os.fstat(source.fileno()).st_size
            start, end, status = 0, size - 1, 200
            requested = self.headers.get("Range")
            if requested:
                match = re.fullmatch(r"bytes=(\d*)-(\d*)", requested)
                if not match or not any(match.groups()):
                    raise ValueError("Unsupported audio range.")
                if match[1]:
                    start = int(match[1]); end = min(size - 1, int(match[2])) if match[2] else size - 1
                else:
                    start = max(0, size - int(match[2]))
                if start > end or start >= size:
                    self._headers(416, "audio/wav", 0)
                    self.send_header("Content-Range", f"bytes */{size}")
                    self.end_headers()
                    return
                status = 206
            self._headers(status, "audio/wav", end - start + 1)
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Disposition", "inline; filename*=UTF-8''" + urllib.parse.quote(path.name))
            if status == 206:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.end_headers()
            source.seek(start)
            remaining = end - start + 1
            while remaining:
                data = source.read(min(65536, remaining))
                if not data:
                    break
                self.wfile.write(data)
                remaining -= len(data)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5002)
    parser.add_argument("--worker", type=Path, default=preview.DEFAULT_WORKER)
    parser.add_argument("--open", action="store_true", help="Open the local web UI in the default browser")
    args = parser.parse_args(argv)
    if not 1 <= args.port <= 65535:
        parser.error("Port must be between 1 and 65535.")
    jobs = Jobs(args.worker)
    try:
        server = Server(args.port, jobs)
    except OSError as error:
        print(f"Cannot start localhost:{args.port}: {error}. Close the other app using this port or choose --port.", file=sys.stderr)
        return 1
    print(f"Logic Preview is running at http://localhost:{args.port} (Ctrl+C to stop).", flush=True)
    if args.open:
        webbrowser.open(f"http://localhost:{args.port}")
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        jobs.close()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
