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
language = sys.argv[2] if len(sys.argv) > 2 else "en"
assert language in ("en", "ru")
def tr(en, ru):
    return ru if language == "ru" else en
payload = bytes(range(256)) * 49152

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
                    time.sleep(0.06)
        except (BrokenPipeError, ConnectionResetError):
            pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory(prefix="deneb-tui-") as root:
    destination = str(Path(root) / "downloads")
    Path(root, "state.json").write_text(json.dumps({"Version": 3, "Settings": {"ActiveFiles": 2, "Connections": 4, "Destination": destination, "Language": language}, "Jobs": []}))
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
        before_help = {p.name: p.read_bytes() for p in Path(root).glob("state.json*")}
        help_result = subprocess.run([binary, "--state-dir", root, "--help"], capture_output=True, text=True, timeout=10)
        assert help_result.returncode == 0
        assert tr("background download manager", "фоновый менеджер загрузок") in help_result.stdout
        assert before_help == {p.name: p.read_bytes() for p in Path(root).glob("state.json*")}
        os.write(master, b"\x1bOP")
        wait_for(tr("Deneb keys", "Клавиши Deneb"))
        pump(0.2)
        os.write(master, b"\x1b")
        pump(0.6)
        # Invalid input must produce a localized error, then return to the input form.
        os.write(master, b"a")
        wait_for(tr("URLs / Android rows", "Ссылки / строки Android"))
        os.write(master, b"ftp://example.org/not-supported\t\t\t\r")
        wait_for(tr("valid HTTP/HTTPS URL", "корректная HTTP/HTTPS-ссылка"))
        pump(0.2)
        os.write(master, b"\r")
        pump(0.3)
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"a")
        wait_for(tr("URLs / Android rows", "Ссылки / строки Android"))
        os.write(master, b"https://example.org/test.bin")
        wait_for("example.org/test.bin")
        # Escape closes the form, then F2 opens settings.
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"\x1bOQ")
        wait_for(tr("Settings", "Настройки"))
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"a")
        wait_for(tr("URLs / Android rows", "Ссылки / строки Android"))
        base = f"http://127.0.0.1:{server.server_port}"
        os.write(master, f"{base}/one.bin\n{base}/two.bin".encode())
        wait_for("two.bin")
        os.write(master, b"\t\t\t\r")
        wait_for(tr("Add to queue?", "Добавить в очередь?"))
        os.write(master, b"\r")
        wait_for(tr("ransferring", "ередача"))
        os.write(master, b"\x1b[2~")
        wait_for("✓")
        os.write(master, b"\x1b[2~")
        pump(0.3)
        os.write(master, b"\x1b[17~")
        wait_for(tr("GLOBAL PAUSE", "ОБЩАЯ ПАУЗА"))
        pump(0.5)
        os.write(master, b"u")
        wait_for(tr("New URL for the same file", "Новый URL того же файла"))
        pump(0.2)
        os.write(master, b"\x1b")
        pump(0.6)
        os.write(master, b"\x1b[3~")
        wait_for(tr("Remove tasks", "Убрать задачи"))
        pump(0.2)
        os.write(master, b"\t\t\r")
        wait_for(tr("Permanently delete", "Безвозвратно удалить"))
        pump(0.2)
        os.write(master, b"\r")
        pump(0.5)
        os.write(master, b"\x1bOS")
        pump(0.3)
        os.write(master, b"\x1bOR")
        pump(0.3)
        os.write(master, b"\x1b[17~")
        pump(0.3)
        os.write(master, b"\r")
        wait_for(tr("Source:", "Источник:"))
        pump(0.3)
        os.write(master, b"\x1b")
        pump(0.6)
        pump(0.5)
        os.write(master, b" ")
        wait_for(tr("Paused", "Ручная пауза"))
        # Switch while the other download is active. The marked paused row must survive.
        pump(0.5)  # let the asynchronous pause finish before accepting another action
        os.write(master, b"\x1b[2~")
        pump(0.3)
        before = json.loads(Path(root, "state.json").read_text())
        paused_id = next(j["Id"] for j in before["Jobs"] if j["State"] == 2)
        active = next(j for j in before["Jobs"] if j["Id"] != paused_id)
        assert active["State"] == 1, before
        before_bytes = sum(s["Committed"] for s in active["Segments"])
        os.write(master, b"\x1bOQ")
        wait_for(tr("Settings", "Настройки"))
        pump(0.2)
        os.write(master, b"\t\t\t")
        pump(0.2)
        os.write(master, b"\x1b[B" if language == "en" else b"\x1b[A")
        pump(0.2)
        os.write(master, b" ")
        pump(0.2)
        # Language -> total rate -> units -> Save. Set a live 512 KiB/s cap.
        os.write(master, b"\t\x1b[H\x1b[3~512\t\t\r")
        language = "ru" if language == "en" else "en"
        wait_for(tr("Paused", "Ручная пауза"))
        pump(2.1)
        after = json.loads(Path(root, "state.json").read_text())
        assert after["Settings"]["Language"] == language, after
        assert after["Settings"]["BandwidthLimitBytesPerSecond"] == 512 * 1024, after
        assert next(j for j in after["Jobs"] if j["Id"] == paused_id)["State"] == 2
        assert next(j for j in after["Jobs"] if j["Id"] != paused_id)["State"] in (1, 3)
        assert sum(s["Committed"] for j in after["Jobs"] if j["Id"] != paused_id for s in j["Segments"]) > before_bytes
        # Moving the cursor must not replace the marked target after a language refresh.
        os.write(master, b"\x1b[B")
        pump(0.2)
        os.write(master, b" ")
        pump(0.5)
        marked_resume = json.loads(Path(root, "state.json").read_text())
        assert next(j for j in marked_resume["Jobs"] if j["Id"] == paused_id)["State"] in (0, 1), marked_resume
        assert next(j for j in marked_resume["Jobs"] if j["Id"] != paused_id)["State"] in (1, 3)
        os.write(master, b" ")
        pump(0.5)
        assert next(j for j in json.loads(Path(root, "state.json").read_text())["Jobs"] if j["Id"] == paused_id)["State"] == 2
        os.write(master, b"\x1b[A")
        pump(0.2)
        # Explicitly remove the mark; a subsequent Space still targets the same row.
        os.write(master, b"\x1b[2~")
        pump(0.2)
        os.write(master, b"q")
        wait_for(tr("Interface closed", "Интерфейс закрыт"))
        process.wait(timeout=10)
        assert process.returncode == 0
        saved = json.loads(Path(root, "state.json").read_text())
        assert len(saved["Jobs"]) == 2
        assert any(job["State"] == 2 for job in saved["Jobs"]), saved
        active_before = sum(s["Committed"] for j in saved["Jobs"] if j["State"] != 2 for s in j["Segments"])
        time.sleep(2.5)
        background = json.loads(Path(root, "state.json").read_text())
        assert sum(s["Committed"] for j in background["Jobs"] if j["State"] != 2 for s in j["Segments"]) > active_before
        assert any(s["Committed"] > 0 for j in saved["Jobs"] for s in j["Segments"])
        os.close(master)
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 35, 120, 0, 0))
        transcript.clear()
        process = subprocess.Popen([binary, "--state-dir", root], stdin=slave, stdout=slave, stderr=slave, env=env, start_new_session=True)
        os.close(slave)
        wait_for(tr("Paused", "Ручная пауза"))
        wait_for(tr("Completed", "Завершение"), 90)
        os.write(master, b" ")
        # Allow shared CI runners to serve the fixture slowly; completion and hashes remain mandatory.
        end = time.monotonic() + 90
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
        wait_for(tr("Clear completed", "Очистить завершённые"))
        os.write(master, b"\t\r")
        pump(0.5)
        os.write(master, b"q")
        wait_for(tr("Interface closed", "Интерфейс закрыт"))
        process.wait(timeout=10)
        assert process.returncode == 0
        assert not json.loads(Path(root, "state.json").read_text())["Jobs"]
        assert all(path.exists() for path in targets)
        print(f"TUI PASS: {sys.argv[2] if len(sys.argv) > 2 else 'en'} -> {language}, live language switch, marks, pause, resume, SHA-256, clear preserves files")
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=5)
        os.close(master)
        subprocess.run([binary, "stop", "--state-dir", root], capture_output=True, timeout=35)
        server.shutdown()
        server.server_close()
