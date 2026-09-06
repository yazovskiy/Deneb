#!/usr/bin/env python3
"""Exercise the real TUI in a disposable PTY (standard library only, macOS/Linux)."""
import errno
import fcntl
import json
import hashlib
import http.server
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import threading
import time

binary = str(Path(sys.argv[1]).resolve())
payload = bytes(range(256)) * 32768

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        start, end = 0, len(payload) - 1
        value = self.headers.get("Range")
        if value:
            left, right = value.removeprefix("bytes=").split("-")
            start, end = int(left), int(right)
        self.send_response(206 if value else 200)
        self.send_header("ETag", '"smoke-v1"')
        self.send_header("Content-Length", str(end - start + 1))
        if value:
            self.send_header("Content-Range", f"bytes {start}-{end}/{len(payload)}")
        self.end_headers()
        try:
            for offset in range(start, end + 1, 65536):
                self.wfile.write(payload[offset:min(offset + 65536, end + 1)])
                self.wfile.flush()
                if end > start:
                    time.sleep(0.04)
        except (BrokenPipeError, ConnectionResetError):
            pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory(prefix="deneb-tui-") as root:
    destination = str(Path(root) / "downloads")
    Path(root, "state.json").write_text(json.dumps({"Version": 1, "Settings": {"ActiveFiles": 2, "Connections": 4, "Destination": destination}, "Jobs": []}))
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 35, 120, 0, 0))
    env = dict(os.environ, TERM="xterm-256color")
    process = subprocess.Popen([binary, "--state-dir", root], stdin=slave, stdout=slave, stderr=slave, env=env, start_new_session=True)
    os.close(slave)
    transcript = bytearray()

    def pump(seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            if select.select([master], [], [], 0.05)[0]:
                transcript.extend(os.read(master, 65536))

    def wait_for(text, timeout=10):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            plain = re.sub(rb"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][A-Z0-9]|\x1b[=>]", b"", transcript)
            if text.encode() in plain:
                transcript.clear()
                return
            if select.select([master], [], [], 0.1)[0]:
                try:
                    chunk = os.read(master, 65536)
                except OSError as error:
                    if error.errno == errno.EIO:
                        break
                    raise
                if not chunk:
                    break
                transcript.extend(chunk)
        raise AssertionError(f"Missing screen text: {text!r}; output: {transcript.decode(errors='replace')[-5000:]}")

    try:
        wait_for("Deneb")
        os.write(master, b"a")
        wait_for("Ссылки / строки Android")
        os.write(master, b"https://example.org/test.bin")
        wait_for("example.org/test.bin")
        # Escape closes the form, then F2 opens settings.
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"\x1bOQ")
        wait_for("Настройки")
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"a")
        wait_for("Ссылки / строки Android")
        base = f"http://127.0.0.1:{server.server_port}"
        os.write(master, f"{base}/one.bin\n{base}/two.bin".encode())
        wait_for("two.bin")
        os.write(master, b"\t\t\t\r")
        wait_for("Добавить в очередь?")
        os.write(master, b"\r")
        wait_for("ередача")
        os.write(master, b"\x1b[2~")
        wait_for("✓")
        os.write(master, b"\x1b[2~")
        pump(0.3)
        os.write(master, b"\x1b[17~")
        wait_for("ОБЩАЯ ПАУЗА")
        pump(0.5)
        os.write(master, b"\x1bOS")
        pump(0.3)
        os.write(master, b"\x1bOR")
        pump(0.3)
        os.write(master, b"\x1b[17~")
        pump(0.3)
        os.write(master, b"\r")
        wait_for("Источник:")
        pump(0.3)
        os.write(master, b"\x1b")
        pump(0.6)
        pump(0.5)
        os.write(master, b" ")
        wait_for("Ручная пауза")
        os.write(master, b"q")
        wait_for("Сохраняю прогресс")
        process.wait(timeout=10)
        assert process.returncode == 0
        saved = json.loads(Path(root, "state.json").read_text())
        assert len(saved["Jobs"]) == 2
        assert sorted(job["State"] for job in saved["Jobs"]) == [0, 2], saved
        assert any(s["Committed"] > 0 for j in saved["Jobs"] for s in j["Segments"])
        os.close(master)
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 35, 120, 0, 0))
        transcript.clear()
        process = subprocess.Popen([binary, "--state-dir", root], stdin=slave, stdout=slave, stderr=slave, env=env, start_new_session=True)
        os.close(slave)
        wait_for("Ручная пауза")
        wait_for("Завершение", 20)
        os.write(master, b" ")
        end = time.monotonic() + 20
        while time.monotonic() < end:
            if select.select([master], [], [], 0.1)[0]:
                transcript.extend(os.read(master, 65536))
            saved = json.loads(Path(root, "state.json").read_text())
            if all(j["State"] == 3 for j in saved["Jobs"]):
                break
        assert all(j["State"] == 3 for j in saved["Jobs"]), saved
        for job in saved["Jobs"]:
            assert hashlib.sha256(Path(job["Target"]).read_bytes()).digest() == hashlib.sha256(payload).digest()
        targets = [Path(job["Target"]) for job in saved["Jobs"]]
        os.write(master, b"\x1b[20~")
        wait_for("Очистить завершённые")
        os.write(master, b"\t\r")
        pump(0.5)
        os.write(master, b"q")
        wait_for("Сохраняю прогресс")
        process.wait(timeout=10)
        assert process.returncode == 0
        assert not json.loads(Path(root, "state.json").read_text())["Jobs"]
        assert all(path.exists() for path in targets)
        print("TUI PASS: marks, reorder, global pause, live details, resume, SHA-256, clear preserves files")
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=5)
        os.close(master)
        server.shutdown()
        server.server_close()
