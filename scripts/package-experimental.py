#!/usr/bin/env python3
"""Package an already published native build, including every runtime sidecar."""
import hashlib
import os
from pathlib import Path
import shutil
import sys
import tarfile
import tempfile
import zipfile

rid = sys.argv[1]
if rid not in ("win-x64", "linux-x64"):
    raise SystemExit("Expected win-x64 or linux-x64")
binary = "deneb.exe" if rid == "win-x64" else "deneb"
published = Path("artifacts") / rid
assert (published / binary).is_file()
cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
output = Path("artifacts/experimental")
output.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix="deneb-package-") as temp:
    staging = Path(temp)
    shutil.copytree(published, staging, dirs_exist_ok=True)
    for name in ("README.md", "README.ru.md", "LICENSE", "CHANGELOG.md", "THIRD-PARTY-NOTICES.md", "EXPERIMENTAL.md"):
        shutil.copy2(name, staging / name)
    shutil.copytree("docs", staging / "docs")
    shutil.copytree("docs/licenses", staging / "licenses")
    for package in (f"microsoft.netcore.app.runtime.{rid}/10.0.8", "system.management/9.0.4", "system.codedom/9.0.4"):
        for notice in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
            shutil.copy2(cache / package / notice, staging / "licenses" / (package.replace("/", "-") + "-" + notice))
    extension = "zip" if rid == "win-x64" else "tar.gz"
    archive = output / f"deneb-{rid}-experimental.{extension}"
    if extension == "zip":
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as target:
            for path in sorted(staging.rglob("*")):
                if path.is_file():
                    target.write(path, path.relative_to(staging))
    else:
        with tarfile.open(archive, "w:gz") as target:
            for path in sorted(staging.iterdir()):
                target.add(path, arcname=path.name)
    with archive.open("rb") as content:
        digest = hashlib.file_digest(content, "sha256").hexdigest()
    (output / f"SHA256SUMS-{rid}.txt").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    # Smoke checks must run the extracted artifact, not the publish directory.
    unpacked = output / f"verify-{rid}"
    unpacked.mkdir(exist_ok=False)
    shutil.unpack_archive(archive, unpacked)
    assert (unpacked / binary).is_file()
    assert (unpacked / "EXPERIMENTAL.md").is_file()
    print(f"Packaged and extracted {archive}: SHA-256 {digest}")
