**English** · [Русский](README.ru.md)

<p align="center">
  <img src="docs/assets/deneb-banner.svg" alt="Deneb — downloads, under control" width="900">
</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/yazovskiy/Deneb?color=8b9cff&style=flat-square"></a>
  <img alt="macOS Apple Silicon" src="https://img.shields.io/badge/macOS-Apple%20Silicon-111827?style=flat-square&logo=apple">
  <a href="https://github.com/yazovskiy/Deneb/releases/tag/v2.0.0-experimental.1"><img alt="Windows / Linux experimental" src="https://img.shields.io/badge/Windows%20%2F%20Linux-experimental-fbbf24?style=flat-square"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-7dd3fc?style=flat-square"></a>
</p>

<p align="center"><b>A fullscreen download manager for your terminal.</b><br>
Queues, parallel connections and safe resume — with a persistent background process.</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz"><b>↓ Download for macOS Apple Silicon</b></a>
  &nbsp; · &nbsp; <a href="docs/usage.ru.md">Full guide (Russian)</a>
  &nbsp; · &nbsp; <a href="CHANGELOG.md">Changelog (Russian)</a>
</p>

## Version 2.0 — background downloads

Closing the interface or Terminal no longer stops downloads. Run `deneb` again to reconnect. **Q / Ctrl+C closes only the interface**; **F10** confirms a full stop.

```sh
./deneb start     # start without a UI
./deneb status    # inspect without starting
./deneb pause     # global pause; manual pauses are preserved
./deneb resume    # remove global pause only
./deneb stop      # save progress and stop the background process
```

All commands accept `--state-dir PATH`. One interface per store is allowed; CLI commands remain available. The background process stays alive when the queue empties. No login autostart or sleep prevention is enabled.

**F2 → Max connections per file** applies to active and future downloads (1–16, default 4). The display shows actual/maximum requests. A smaller number can mean unsupported Range/validators, a file below 16 MiB, a small remainder or retry delays. Changing the maximum preserves downloaded bytes and pauses.

If the connection is lost, **F11** reconnects; **F12** starts the background process if absent and reconnects. Commands with uncertain results are not automatically replayed.

## Downloads and platform support

| Platform | Status | Self-contained download |
|---|---|---|
| macOS Apple Silicon (ARM64) | **Stable** | [tar.gz](https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz) |
| Windows x64 | **Experimental — pre-release** | [zip](https://github.com/yazovskiy/Deneb/releases/download/v2.0.0-experimental.1/deneb-win-x64-experimental.zip) |
| Linux x64 (glibc) | **Experimental — pre-release** | [tar.gz](https://github.com/yazovskiy/Deneb/releases/download/v2.0.0-experimental.1/deneb-linux-x64-experimental.tar.gz) |

Windows/Linux builds are published in a [separate pre-release](https://github.com/yazovskiy/Deneb/releases/tag/v2.0.0-experimental.1); the stable macOS release remains Latest. Automated checks do not replace full terminal UX acceptance. **Open file, reveal in file manager and copy path are currently macOS-only.** Windows/Linux ARM64, Intel Macs and Alpine/musl builds are not included.

Extract the archive before running: `./deneb` on Linux or `.\deneb.exe` in Windows PowerShell (Windows Terminal recommended). No .NET installation is needed; OS-level .NET runtime dependencies are still required on Linux. Experimental archives include `EXPERIMENTAL.md` with limitations; SHA-256 files are attached to their pre-release. Use a separate `--state-dir` when trying them. Binaries are unsigned; do not disable system security globally.

## Interface language

English is the default, including after an upgrade from 1.1. Open **F2 → Language / Язык**, choose **English** or **Русский** using the arrow keys and Space, then save. The interface changes immediately without interrupting downloads, and your choice is remembered.

State is migrated to v4 with a separate backup of the original version. Unrecognized historical errors are labeled “Message from a previous version”. File names and URLs are never translated.

## Quick start

1. Download the [ready-to-run archive](https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz) and extract it.
2. Open Terminal in the extracted folder and run `./deneb`.
3. Press **A**, paste your URLs and confirm. **F1** shows all shortcuts.

**No .NET installation required:** the runtime is included. This binary targets macOS Apple Silicon (ARM64), not Intel Macs. A terminal size of at least 120×35 is recommended.

Download from your terminal:

```sh
mkdir -p deneb-download && cd deneb-download
curl -fL -O https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz
curl -fL -O https://github.com/yazovskiy/Deneb/releases/latest/download/SHA256SUMS.txt
shasum -a 256 -c SHA256SUMS.txt
tar -xzf deneb-osx-arm64.tar.gz
./deneb
```

The build is not Developer ID-signed or notarized by Apple. If macOS blocks it, verify the source and checksum, then allow this specific file in system security settings. Do not disable macOS security globally.

## Features

| Feature | Behavior |
|---|---|
| **Queue control** | Reorder tasks, choose the next download, and act on marked rows. |
| **Independent pauses** | Global pause preserves manual pauses. Queue order and state survive restarts. |
| **Parallel downloads** | Two active files and up to four connections per file by default. |
| **Safe resume** | Actual Range and HTTP validator checks; changed remote data is never appended to the old file. |
| **Live details** | Segment progress, retries, countdowns, smoothed speed and ETA. |
| **File actions** | Open a file, reveal it in Finder, copy its path, or clear completed entries without deleting files. |
| **Flexible input** | Direct HTTP/HTTPS URLs, Markdown links and pasted Android Download Manager rows. |
| **English and Russian** | Switch in settings without stopping active transfers. |

Android import takes **only the URL and name**, not Android's downloaded-byte count. Full URLs with tokens are hidden from the table and details, but are stored locally for resuming.

## Shortcuts

| Key | Action |
|---|---|
| ↑ / ↓ · `Insert` | Select a row · mark it |
| `A` · `U` | Add URLs · replace URL |
| `Space` · `F6` | Pause/resume selected · global pause |
| `F3` / `F4` · `F5` | Move up/down · download next |
| `Enter` · `F2` | Live details · settings and language |
| `O` · `F7` · `F8` | Open file · Finder · copy path |
| `Delete` · `F9` | Remove selected · clear completed |
| `F1` · `Q` / `Ctrl+C` | Help · close interface only |

Mac keyboards may require **Fn** for function keys. If Insert is unavailable, configure an equivalent key in your terminal.

## Your files stay yours

- Default destination: `~/Downloads/Deneb`.
- Queue and settings: `~/Library/Application Support/Deneb` on macOS; `%LOCALAPPDATA%\Deneb` on Windows; the .NET local application data directory plus `Deneb` on Linux (normally `~/.local/share/Deneb`). Override with `--state-dir`.
- Existing files are never overwritten. Removing tasks preserves partial data by default; deleting it requires separate confirmation.
- Stop the old version before upgrading (close 1.x; use `deneb stop` for 2.x). Migration preserves `state.json.v1.bak`, `.v2.bak` or `.v3.bak` without renaming existing parts.
- A metadata backup alone does **not** make rollback to 1.2 safe after ranges have been split. Do not open a v4 store with an older binary.
- Segment assembly temporarily needs about twice the file size in free space.

HTTP/HTTPS only. No torrents, website URL extraction, ADB integration or custom Cookie/Authorization headers. Downloads require the background process and an awake computer.

## Development

Use SDK **.NET 10.0.300** (or a compatible patch as specified by `global.json`).

```sh
git clone https://github.com/yazovskiy/Deneb.git
cd Deneb
dotnet restore Deneb.slnx --locked-mode
dotnet test Deneb.slnx --no-restore
dotnet run --project src/Deneb.App
```

`Deneb.Core` has no UI or language dependencies: phases and diagnostics are typed values. `Deneb.Control` contains the versioned local protocol, client, server and platform-specific background launcher. Unix sockets and Windows named pipes are restricted to the current user. `Deneb.App` renders those values using built-in EN/RU resources and Terminal.Gui. xUnit tests use a local HTTP server with deterministic files.

Run both terminal scenarios after building:

```sh
python3 scripts/tui-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb en
python3 scripts/tui-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb ru
python3 scripts/dialog-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb
python3 scripts/background-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb
```

Build a self-contained release archive on a Mac ARM64:

```sh
dotnet publish src/Deneb.App -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -o artifacts/osx-arm64
bash scripts/package-release.sh
```

Tests cover transfers, state recovery, localization resources and formatting. PTY scenarios cover live language switching, pauses, restart, SHA-256 and clearing entries without deleting files. Native macOS file actions are tested through a replaceable adapter; see the [manual checklist (Russian)](docs/usage.ru.md).

## Feedback and license

[Report an issue](https://github.com/yazovskiy/Deneb/issues/new/choose) with the version, reproduction steps and error message. **Do not post private URLs, tokens or `state.json`.**

Deneb uses the [MIT license](LICENSE). Dependencies retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md).
