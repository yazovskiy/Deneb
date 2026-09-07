**English** · [Русский](README.ru.md)

<p align="center">
  <img src="docs/assets/deneb-banner.svg" alt="Deneb — downloads, under control" width="900">
</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/yazovskiy/Deneb?color=8b9cff&style=flat-square"></a>
  <img alt="macOS Apple Silicon" src="https://img.shields.io/badge/macOS-Apple%20Silicon-111827?style=flat-square&logo=apple">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-7dd3fc?style=flat-square"></a>
</p>

<p align="center"><b>A fullscreen download manager for your terminal.</b><br>
Queues, parallel connections and safe resume — without a browser or background service.</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz"><b>↓ Download for macOS Apple Silicon</b></a>
  &nbsp; · &nbsp; <a href="docs/usage.ru.md">Full guide (Russian)</a>
  &nbsp; · &nbsp; <a href="CHANGELOG.md">Changelog (Russian)</a>
</p>

## Interface language — version 1.2

English is the default, including after an upgrade from 1.1. Open **F2 → Language / Язык**, choose **English** or **Русский** using the arrow keys and Space, then save. The interface changes immediately without interrupting downloads, and your choice is remembered.

State is migrated to v3 with a separate backup of the original version. Unrecognized historical errors are labeled “Message from a previous version”. File names and URLs are never translated.

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
| `F1` · `Q` / `Ctrl+C` | Help · graceful exit |

Mac keyboards may require **Fn** for function keys. If Insert is unavailable, configure an equivalent key in your terminal.

## Your files stay yours

- Default destination: `~/Downloads/Deneb`.
- Queue and settings: `~/Library/Application Support/Deneb`.
- Existing files are never overwritten. Removing tasks preserves partial data by default; deleting it requires separate confirmation.
- Upgrading from 1.0/1.1 preserves `state.json.v1.bak` / `state.json.v2.bak`. Close the old version first.
- Segment assembly temporarily needs about twice the file size in free space.

HTTP/HTTPS only. No daemon, torrents, website URL extraction, ADB integration or custom Cookie/Authorization headers. Downloads run while the process is open.

## Development

Use SDK **.NET 10.0.300** (or a compatible patch as specified by `global.json`).

```sh
git clone https://github.com/yazovskiy/Deneb.git
cd Deneb
dotnet restore Deneb.slnx --locked-mode
dotnet test Deneb.slnx --no-restore
dotnet run --project src/Deneb.App
```

`Deneb.Core` has no UI or language dependencies: phases and diagnostics are typed values. `Deneb.App` renders those values using built-in EN/RU resources and Terminal.Gui. xUnit tests use a local HTTP server with deterministic files.

Run both terminal scenarios after building:

```sh
python3 scripts/tui-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb en
python3 scripts/tui-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb ru
python3 scripts/dialog-smoke.py src/Deneb.App/bin/Debug/net10.0/deneb
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
