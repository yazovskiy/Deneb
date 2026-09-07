#!/usr/bin/env python3
"""Check long diagnostic scrolling and decision/completed dialogs in a 120x35 PTY."""
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
    for completed in (False, True):
        with tempfile.TemporaryDirectory(prefix="deneb-dialog-") as root:
            target = Path(root, "Звезда с пробелами.bin")
            target.write_bytes(b"test")
            # Legacy is code 37 in schema v3; intentionally exercise preserved historical text.
            job = {"Id": "2b656665-d457-44ea-82c4-b152c8d06c49", "Name": target.name,
                   "Url": "https://example.org/file", "Destination": root, "Target": str(target),
                   "State": 3 if completed else 4, "Total": 4,
                   "Diagnostic": None if completed else {"Code": 37, "LegacyText": "historical diagnostic " * 150 + " LONG-MESSAGE-END"},
                   "Segments": []}
            Path(root, "state.json").write_text(json.dumps({"Version": 3, "GloballyPaused": True,
                "Settings": {"Language": language, "Destination": root}, "Jobs": [job]}))
            master, slave = pty.openpty()
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 35, 120, 0, 0))
            process = subprocess.Popen([binary, "--state-dir", root], stdin=slave, stdout=slave, stderr=slave,
                                       env=dict(os.environ, TERM="xterm-256color"), start_new_session=True)
            os.close(slave)
            output = bytearray()

            def pump(seconds=0.5):
                end = time.monotonic() + seconds
                while time.monotonic() < end:
                    if select.select([master], [], [], 0.05)[0]:
                        output.extend(os.read(master, 65536))

            def plain():
                return re.sub(rb"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][A-Z0-9]|\x1b[=>]", b"", output).decode(errors="replace")

            def expect(text, timeout=10):
                end = time.monotonic() + timeout
                while time.monotonic() < end:
                    if text in plain():
                        return
                    pump(0.1)
                raise AssertionError(f"Missing {text!r}: {plain()[-4000:]}")

            try:
                expect("Deneb")
                pump()
                output.clear()
                os.write(master, b"\r")
                expect("Download details" if language == "en" else "Подробности загрузки")
                pump()
                for label in (("Close", "Start over", "Open file", "Show in Finder", "Copy path") if language == "en"
                              else ("Закрыть", "Начать заново", "Открыть файл", "Показать в Finder", "Скопировать путь")):
                    expect(label)
                if not completed:
                    # Reach the scrollable text with keyboard navigation (buttons can own initial focus).
                    output.clear()
                    for _ in range(6):
                        os.write(master, b"\x1b[6~" * 15)
                        pump(0.3)
                        if "LONG-MESSAGE-END" in plain():
                            break
                        os.write(master, b"\t")
                        pump(0.2)
                    expect("LONG-MESSAGE-END")
                    pump(1.0)  # progress refresh must not reset scrolling
                    os.write(master, b"\x1b")
                    pump()
                    os.write(master, b"\r")
                    pump()
                    output.clear()
                    for _ in range(3):  # Details text -> segments -> Close -> Start over
                        os.write(master, b"\t")
                        pump(0.2)
                    os.write(master, b"\r")  # confirmation defaults to Cancel
                    expect("Old parts will be saved separately." if language == "en" else "Старые части будут сохранены отдельно.")
                    os.write(master, b"\r")
                    pump()
                os.write(master, b"\x1b")
                pump()
                output.clear()
                os.write(master, b"q")
                expect("Saving progress" if language == "en" else "Сохраняю прогресс")
                process.wait(timeout=10)
                assert process.returncode == 0
                saved = json.loads(Path(root, "state.json").read_text())
                assert saved["Jobs"][0]["State"] == job["State"]
                assert target.read_bytes() == b"test"
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    process.wait(timeout=5)
                os.close(master)
print("DIALOG PASS: EN/RU, 120x35, all detail buttons, long error scrolling, restart cancellation")
