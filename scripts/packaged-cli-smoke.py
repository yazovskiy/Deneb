#!/usr/bin/env python3
"""Native packaged CLI checks with deadlines and no inherited capture pipes."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

binary = str(Path(sys.argv[1]).resolve())
with tempfile.TemporaryDirectory(prefix="deneb-packaged-cli-") as temp:
    root = Path(temp)
    store = root / "state"
    store.mkdir()
    state = store / "state.json"
    sequence = 0

    def run(*args):
        global sequence
        sequence += 1
        print(f"CLI check {sequence}: {' '.join(args)}", flush=True)
        # A background descendant may inherit handles on Windows. A regular file
        # lets us wait for the CLI process, without waiting for pipe EOF from it.
        log = root / f"command-{sequence}.log"
        with log.open("wb") as output:
            result = subprocess.run([binary, *args, "--state-dir", str(store)],
                                    stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                                    timeout=45)
        text = log.read_text(encoding="utf-8-sig", errors="replace")
        assert result.returncode == 0, f"CLI {args} failed ({result.returncode}): {text}"
        return text

    try:
        assert "2.1.0" in run("--version")
        for language in ("en", "ru"):
            state.write_text(json.dumps({"Version": 4, "Settings": {"Language": language}, "Jobs": []}), encoding="utf-8")
            before = state.read_bytes()
            help_text = run("--help")
            assert ("Использование:" if language == "ru" else "Usage:") in help_text
            assert before == state.read_bytes(), "Help modified the store"
        for command in ("start", "start", "pause", "status", "resume", "stop", "stop"):
            run(command)
        print("PACKAGED CLI PASS: EN/RU, read-only help, start/idempotency/pause/status/resume/stop", flush=True)
    finally:
        run("stop")
