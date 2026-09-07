#!/usr/bin/env python3
"""macOS/Linux: real detached process, terminal loss, dynamic requests and crash recovery."""
import concurrent.futures
import fcntl
import getpass
import hashlib
import http.server
import json
import os
from pathlib import Path
import pty
import select
import re
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import termios
import threading
import time
import uuid

binary = str(Path(sys.argv[1]).resolve())
payload = bytes(range(256)) * 262144
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        left, right = self.headers.get("Range", f"bytes=0-{len(payload)-1}")[6:].split("-")
        start, end = int(left), int(right)
        self.send_response(206)
        self.send_header("ETag", '"background-test"')
        self.send_header("Content-Range", f"bytes {start}-{end}/{len(payload)}")
        self.send_header("Content-Length", str(end-start+1))
        self.end_headers()
        try:
            for offset in range(start, end+1, 65536):
                self.wfile.write(payload[offset:min(end+1, offset+65536)])
                self.wfile.flush()
                if end > start: time.sleep(float(os.environ.get("DENEB_SMOKE_DELAY", "0.03")))
        except (BrokenPipeError, ConnectionResetError): pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory(prefix="deneb-background-") as root:
    digest = hashlib.sha256((getpass.getuser()+"|"+root).encode()).hexdigest().upper()[:24]
    endpoint = str(Path(tempfile.gettempdir(), "deneb-"+digest, "control.sock"))
    def cli(command):
        result = subprocess.run([binary, command, "--state-dir", root], capture_output=True, text=True, timeout=40)
        assert result.returncode == 0, result.stderr
        return result.stdout
    class Client:
        def __init__(self):
            self.stream = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.stream.settimeout(35)
            self.stream.connect(endpoint)
            self.call(0)
        def read(self, count):
            data = b""
            while len(data) < count:
                block = self.stream.recv(count-len(data))
                assert block, "unexpected disconnect"
                data += block
            return data
        def call(self, command, **values):
            data = json.dumps(dict(Id=str(uuid.uuid4()), Protocol=1, Version="2.0.0", Command=command, **values)).encode()
            self.stream.sendall(struct.pack("<i", len(data))+data)
            result = json.loads(self.read(struct.unpack("<i", self.read(4))[0]))
            assert result["Error"] is None, result
            return result["State"]
        def close(self): self.stream.close()
    client = None
    ui = None
    master = None
    try:
        with concurrent.futures.ThreadPoolExecutor() as pool:
            assert len(list(pool.map(lambda _: cli("start"), range(2)))) == 2
        client = Client()
        state = client.call(2, Input={"Url":f"http://127.0.0.1:{server.server_port}/data.bin"}, Destination=root)
        job_id = state["Jobs"][0]["Id"]
        def wait(predicate, timeout=40):
            deadline = time.monotonic()+timeout
            while time.monotonic() < deadline:
                # Keep consuming the terminal while polling IPC. Otherwise PTY backpressure
                # blocks rendering and prevents the UI's control heartbeat on slow runners.
                if master is not None:
                    while select.select([master], [], [], 0)[0]:
                        if not os.read(master, 65536): break
                state = client.call(1)
                assert not any(j["State"] in (4,5) for j in state["Jobs"]), state
                if predicate(state): return state
                time.sleep(0.1)
            raise AssertionError(state)
        wait(lambda s:s["Jobs"][0]["Connections"] == 4)
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH",35,120,0,0))
        ui = subprocess.Popen([binary,"--state-dir",root],stdin=slave,stdout=slave,stderr=slave,
            env=dict(os.environ,TERM="xterm-256color"),start_new_session=True)
        os.close(slave)
        time.sleep(0.5)
        before = client.call(1)["Jobs"][0]["Bytes"]
        os.killpg(ui.pid, signal.SIGHUP)
        ui.wait(timeout=10)
        os.close(master); master = None
        wait(lambda s:s["Jobs"][0]["Bytes"] > before+1048576)
        settings = client.call(1)["Settings"]
        settings["Connections"] = 10
        client.call(3, Settings=settings)
        wait(lambda s:s["Jobs"][0]["Connections"] == 10)
        settings["Connections"] = 2
        client.call(3, Settings=settings)
        wait(lambda s:s["Jobs"][0]["Connections"] == 2 and not s["Jobs"][0]["ApplyingConnections"])
        state = client.call(1)
        daemon_pid = state["ProcessId"]
        assert daemon_pid != os.getpid() and daemon_pid != ui.pid
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH",35,120,0,0))
        ui = subprocess.Popen([binary,"--state-dir",root],stdin=slave,stdout=slave,stderr=slave,
            env=dict(os.environ,TERM="xterm-256color"),start_new_session=True)
        os.close(slave)
        def screen(text, timeout=15):
            output = b""
            deadline = time.monotonic()+timeout
            while time.monotonic() < deadline:
                if select.select([master], [], [], 0.1)[0]:
                    output += os.read(master, 65536)
                    plain = re.sub(rb"\x1b\[[0-?]*[ -/]*[@-~]", b"", output)
                    if text.encode() in plain: return
            raise AssertionError(output[-3000:])
        screen("Deneb")
        # PID comes from the authenticated channel of this disposable test store.
        os.kill(daemon_pid, signal.SIGKILL)
        client.close(); client = None
        screen("Disconnected")
        os.write(master, b"\x1b[24~")  # F12: explicit restart/reconnect
        time.sleep(1)
        client = Client()
        # Shared CI runners can serve this 64 MiB fixture below 1 MiB/s after retries.
        # Keep a bounded deadline without weakening the final state/hash checks.
        state = wait(lambda s:s["Jobs"][0]["State"] == 3, 120)
        target = Path(state["Jobs"][0]["Target"])
        assert hashlib.sha256(target.read_bytes()).digest() == hashlib.sha256(payload).digest()
        # Drain progress before the next key, then stop through the actual UI.
        while select.select([master], [], [], 0)[0]: os.read(master, 65536)
        os.write(master, b"\x1b[21~")
        screen("Stop downloads")
        os.write(master, b"\t")
        time.sleep(0.2)
        os.write(master, b"\r")
        screen("Interface closed")
        ui.wait(timeout=10)
        assert ui.returncode == 0
        os.close(master); master = None
        client.close(); client = None
        cli("start")
        client = Client()
        cli("pause"); cli("stop")
        client.close(); client = None
        cli("start")
        client = Client()
        assert client.call(1)["GloballyPaused"]
        cli("resume")
        assert not client.call(1)["GloballyPaused"]
        print("BACKGROUND PASS: racing starts, terminal SIGHUP, 4->10->2, daemon SIGKILL/F12 recovery, SHA-256, F10 stop and pause")
    finally:
        if client: client.close()
        if ui and ui.poll() is None: os.killpg(ui.pid, signal.SIGTERM); ui.wait(timeout=10)
        if master is not None: os.close(master)
        cli("stop")
        server.shutdown(); server.server_close()
