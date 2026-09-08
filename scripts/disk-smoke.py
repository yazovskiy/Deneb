#!/usr/bin/env python3
"""macOS acceptance on a disposable 640 MiB sparse image, never the host volume."""
import hashlib
import getpass
import http.server
import json
import os
from pathlib import Path
import subprocess
import socket
import struct
import sys
import tempfile
import threading
import time
import uuid

if sys.platform != "darwin":
    raise SystemExit("This acceptance scenario requires macOS hdiutil")
binary = str(Path(sys.argv[1]).resolve())
payload = bytes(range(256)) * 65536

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        start, end = 0, len(payload) - 1
        value = self.headers.get("Range")
        if value:
            left, right = value.removeprefix("bytes=").split("-")
            start, end = int(left), int(right)
        self.send_response(206 if value else 200)
        self.send_header("ETag", '"disk-test"')
        self.send_header("Content-Length", str(end - start + 1))
        if value: self.send_header("Content-Range", f"bytes {start}-{end}/{len(payload)}")
        self.end_headers()
        try: self.wfile.write(payload[start:end + 1])
        except (BrokenPipeError, ConnectionResetError): pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory(prefix="deneb-disk-acceptance-") as temp:
    root = Path(temp)
    mount = root / "volume"
    store = root / "state"
    mount.mkdir(); store.mkdir()
    image = root / "test.sparseimage"
    attached = False
    def cli(command):
        subprocess.run([binary, command, "--state-dir", str(store)], check=True, timeout=45)
    def wait_for(state):
        end = time.monotonic() + 30
        while time.monotonic() < end:
            job = json.loads((store / "state.json").read_text())["Jobs"][0]
            if job["State"] == state: return job
            assert job["State"] not in (4, 5), job
            time.sleep(0.1)
        raise AssertionError(f"Expected state {state}: {job}")
    def resume_job(job_id):
        digest = hashlib.sha256((getpass.getuser() + "|" + str(store)).encode()).hexdigest().upper()[:24]
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(10)
            connection.connect(str(Path(tempfile.gettempdir(), "deneb-" + digest, "control.sock")))
            def receive(count):
                data = b""
                while len(data) < count:
                    block = connection.recv(count - len(data))
                    assert block, "Unexpected control disconnect"
                    data += block
                return data
            for command in (0, 8):  # Hello, Resume selected task (not global ResumeAll).
                data = json.dumps({"Id": str(uuid.uuid4()), "Protocol": 3, "Version": "2.2.0", "Command": command, "Ids": [job_id]}).encode()
                connection.sendall(struct.pack("<i", len(data)) + data)
                response = json.loads(receive(struct.unpack("<i", receive(4))[0]))
                assert response["Error"] is None, response
                if command == 8: assert response["Batch"]["Processed"] == [job_id], response
    try:
        subprocess.run(["hdiutil", "create", "-size", "640m", "-fs", "APFS", "-type", "SPARSE", "-volname", "Deneb21Test", str(image)], check=True, timeout=60)
        subprocess.run(["hdiutil", "attach", str(image), "-mountpoint", str(mount), "-nobrowse"], check=True, timeout=60)
        attached = True
        space = os.statvfs(mount)
        assert space.f_blocks * space.f_frsize < 1024 ** 3, "Refuse to fill a large filesystem"
        assert os.stat(mount).st_dev != os.stat(root).st_dev, "Refuse to fill the host filesystem"
        fill = space.f_bavail * space.f_frsize - (512 * 1024 ** 2 + len(payload))
        assert 0 < fill < 256 * 1024 ** 2, fill
        filler = mount / "acceptance-only-padding.bin"
        with filler.open("xb") as output:
            while fill:
                block = min(fill, 1024 ** 2)
                output.write(os.urandom(block)); fill -= block
            output.flush(); os.fsync(output.fileno())
        job_id = str(uuid.uuid4())
        (store / "state.json").write_text(json.dumps({"Version": 5, "Settings": {"Destination": str(mount)}, "Jobs": [
            {"Id": job_id, "Url": f"http://127.0.0.1:{server.server_port}/disk.bin", "Name": "disk.bin", "Destination": str(mount)}]}))
        cli("start")
        paused = wait_for(2)
        assert paused["Diagnostic"] is not None
        assert sum(s["Committed"] for s in paused["Segments"]) == 0
        filler.unlink()  # Only our disposable padding file on the test image.
        cli("resume"); time.sleep(1)
        assert json.loads((store / "state.json").read_text())["Jobs"][0]["State"] == 2
        cli("stop")
        cli("start")
        assert json.loads((store / "state.json").read_text())["Jobs"][0]["State"] == 2
        resume_job(job_id)
        done = wait_for(3)
        assert hashlib.sha256(Path(done["Target"]).read_bytes()).digest() == hashlib.sha256(payload).digest()
        print("DISK PASS: real constrained volume, protected pause, no global auto-resume, restart/resume and SHA-256", flush=True)
    finally:
        cli("stop")
        server.shutdown(); server.server_close()
        if attached: subprocess.run(["hdiutil", "detach", str(mount)], check=True, timeout=60)
