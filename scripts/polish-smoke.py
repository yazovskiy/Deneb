#!/usr/bin/env python3
"""Isolated EN/RU PTY: resize, names, help tabs and safe deletion cancellation."""
import fcntl
import json
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
import time

binary = str(Path(sys.argv[1]).resolve())
for language in ("en", "ru"):
    def tr(en, ru): return ru if language == "ru" else en
    with tempfile.TemporaryDirectory(prefix="deneb-polish-pty-") as root:
        target = Path(root, "fixture.bin")
        target.write_bytes(b"polish fixture")
        jobs = [{"Id": "2b656665-d457-44ea-82c4-b152c8d06c49", "Name": "Длинное имя с пробелами " * 20 + "NAME-END.bin",
                 "Url": "https://example.org/fixture", "Destination": root, "Target": str(target), "State": 3, "Total": 14, "Segments": []},
                {"Id": "3b656665-d457-44ea-82c4-b152c8d06c49", "Name": "paused.bin", "Url": "https://example.org/paused", "Destination": root, "State": 2, "Segments": []}]
        state = Path(root, "state.json")
        state.write_text(json.dumps({"Version": 5, "GloballyPaused": True, "Settings": {"Language": language, "Destination": root}, "Jobs": jobs}))
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 35, 120, 0, 0))
        process = subprocess.Popen([binary, "--state-dir", root], stdin=slave, stdout=slave, stderr=slave,
                                   env=dict(os.environ, TERM="xterm-256color"), start_new_session=True)
        os.close(slave)
        output = bytearray()

        def pump(seconds=0.5):
            end = time.monotonic() + seconds
            while time.monotonic() < end:
                if select.select([master], [], [], 0.05)[0]: output.extend(os.read(master, 65536))

        def plain():
            return re.sub(rb"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][A-Z0-9]|\x1b[=>]", b"", output).decode(errors="replace")

        def expect(text, timeout=10):
            end = time.monotonic() + timeout
            while time.monotonic() < end:
                if text in plain(): return
                pump(0.1)
            raise AssertionError(f"Missing {text!r}: {plain()[-5000:]}")

        def send(value):
            os.write(master, value)
            pump()

        try:
            expect("Длинное имя")
            pump()
            send(b"\x1b[2~")
            expect("✓")
            for width, height in ((80, 24), (160, 45), (120, 35)):
                output.clear()
                fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", height, width, 0, 0))
                os.kill(process.pid, signal.SIGWINCH)
                expect("Длинное имя")
                expect("✓")
                expect("…")
                pump()
            output.clear()
            send(b"\x1bOP")
            expect(tr("Keyboard", "Клавиши"))
            expect(tr("CLI commands", "Команды CLI"))
            send(b"\x1b[Z")  # Shift+Tab reaches the tab bar
            send(b"\x1b[C")
            expect(tr("Usage:", "Использование:"))
            send(b"\t")
            send(b"\x1b[B")  # command list -> add
            expect("--destination")
            send(b"\x1b")
            # Mixed selection, both destructive confirmations default to Cancel.
            send(b"\x1b[B")
            send(b"\x1b[2~")
            output.clear()
            send(b"\x1b[3~")
            expect(tr("Remove from list", "Убрать из списка"))
            expect(tr("Delete unfinished data", "Удалить незавершённые данные"))
            expect(tr("Move to Trash and remove from list", "В корзину и убрать из списка"))
            send(b"\t\t\r")
            expect(tr("Permanently delete", "Безвозвратно удалить"))
            send(b"\r")
            if sys.platform == "darwin":
                output.clear()
                send(b"\t\r")
                expect(tr("system Trash", "системную корзину"))
                send(b"\r")
            send(b"\x1b")
            output.clear()
            send(b"q")
            expect(tr("Interface closed", "Интерфейс закрыт"))
            process.wait(timeout=10)
            assert len(json.loads(state.read_text())["Jobs"]) == 2
            assert target.read_bytes() == b"polish fixture"
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                process.wait(timeout=5)
            subprocess.run([binary, "stop", "--state-dir", root], capture_output=True, timeout=35)
            os.close(master)
print("POLISH PTY PASS: EN/RU, resize 80/120/160, marks, names, F1 tabs, cancellation preserves data")
