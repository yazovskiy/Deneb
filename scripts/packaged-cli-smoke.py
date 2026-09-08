#!/usr/bin/env python3
"""Native packaged CLI checks with deadlines and no inherited capture pipes."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import hashlib
import http.server
import threading
import time

binary = str(Path(sys.argv[1]).resolve())
with tempfile.TemporaryDirectory(prefix="deneb-packaged-cli-") as temp:
    root = Path(temp)
    store = root / "state"
    store.mkdir()
    state = store / "state.json"
    sequence = 0

    def run(*args, expected=0, input_text=None):
        global sequence
        sequence += 1
        print(f"CLI check {sequence}: {args[0]}", flush=True)
        # A background descendant may inherit handles on Windows. A regular file
        # lets us wait for the CLI process, without waiting for pipe EOF from it.
        log = root / f"command-{sequence}.log"
        with log.open("wb") as output:
            result = subprocess.run([binary, *args, "--state-dir", str(store)],
                                    input=input_text.encode() if input_text is not None else b"", stdout=output, stderr=output,
                                    timeout=45)
        text = log.read_text(encoding="utf-8-sig", errors="replace")
        assert result.returncode == expected, f"CLI check {sequence} failed ({result.returncode}): {text}"
        return text

    def command(*args, **kwargs):
        response = json.loads(run(*args, "--json", **kwargs))
        assert response["schemaVersion"] == 1
        return response

    try:
        assert "2.2.0" in run("--version")
        for language in ("en", "ru"):
            state.write_text(json.dumps({"Version": 4, "Settings": {"Language": language}, "Jobs": []}), encoding="utf-8")
            before = state.read_bytes()
            help_text = run("--help")
            assert ("Использование:" if language == "ru" else "Usage:") in help_text
            assert before == state.read_bytes(), "Help modified the store"
            for name in ("add", "list", "show", "pause", "resume", "remove"):
                assert ("Использование:" if language == "ru" else "Usage:") in run(name, "--help")
                assert before == state.read_bytes()
        assert not command("list")["result"]["backgroundRunning"]
        command("add", "invalid", expected=2)
        assert not command("list")["result"]["backgroundRunning"]
        for action in ("start", "start", "pause", "status", "resume", "stop", "stop"):
            run(action)
        # The public CLI drives a real, isolated download and verifies its bytes.
        payload = bytes(range(256)) * 8192
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.send_header("ETag", '"cli22"')
                self.end_headers()
                try: self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError): pass
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            run("start"); run("pause")
            base = f"http://127.0.0.1:{server.server_port}"
            data = json.loads(run("add", "--stdin", "--destination", str(root / "files"), "--json", input_text=f"Row: 1 _id=1, title=Файл.bin, uri={base}/a?token=secret, status=200, bytes_so_far=123\n{base}/b"))
            ids = data["result"]["added"]
            assert len(ids) == 2
            listing = json.loads(run("list", "--json"))["result"]
            assert listing["globallyPaused"] and all(j["bytes"] == 0 for j in listing["jobs"])
            assert "secret" not in json.dumps(listing)
            assert len(json.loads(run("list", "--search", "файл", "--json"))["result"]["jobs"]) == 1
            run("pause", ids[0])
            run("resume")
            run("show", ids[0].replace("-", "")[:8], "--json")
            run("remove", ids[0], "--delete-partial", "--json", expected=2)
            run("resume", ids[0])
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                jobs = json.loads(run("list", "--json"))["result"]["jobs"]
                if all(j["state"] == "Completed" for j in jobs): break
                time.sleep(0.3)
            else: raise AssertionError("CLI download did not complete")
            paths = [Path(j["target"]) for j in jobs]
            for path in paths: assert hashlib.sha256(path.read_bytes()).digest() == hashlib.sha256(payload).digest()
            run("remove", *ids, "--delete-partial", "--yes", "--json")
            assert all(path.exists() for path in paths)
        finally:
            server.shutdown(); server.server_close()
        print("PACKAGED CLI PASS: EN/RU, read-only help, start/idempotency/pause/status/resume/stop", flush=True)
    finally:
        run("stop")
