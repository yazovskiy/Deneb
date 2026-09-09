# Deneb 2.2 CLI / JSON contract

All commands accept `--state-dir PATH`. Paths resolve against the CLI working directory. Only `start`, `add`, and launching the UI start an absent daemon. One UI lease does not prevent CLI clients. No command is automatically retried after a lost response.

## Commands

- `add URL...` or `add --stdin`: direct HTTP/HTTPS URLs; stdin also accepts existing Android/Markdown input syntax. Modes are exclusive. `--destination PATH` applies to the group; `--name NAME` requires one item. Android byte counters are ignored. Validate the entire input before sending; add sequentially until failure, without rolling back earlier successes. Inputs are numbered from 1. Duplicate URLs intentionally create separate tasks.
- `list [--search TEXT] [--filter all|active|queued|paused|completed|attention]`: filename substring, ordinal case-insensitive, combined with state filter; queue order is preserved. Attention includes Failed, NeedsDecision and disk-space/measurement pauses. An absent daemon returns an empty result with `backgroundRunning: false`, not an offline view of saved jobs.
- `show ID`: task details and segments.
- `pause [ID...]`, `resume [ID...]`: no IDs means global pause/resume; IDs mean individual tasks. Resume skips completed/decision-required tasks and retries failed ones without changing global pause.
- `remove ID...`: removes entries from the list, preserving files and parts. `--delete-partial --yes` deletes unfinished parts and their entries, skipping completed downloads. `--trash --yes` moves completed regular files to macOS system Trash and removes their entries, skipping unfinished downloads. These modes are mutually exclusive; either requires `--yes`. Trash is unavailable on Windows/Linux, and never falls back to permanent deletion.
- `start`, `status`, `stop`: existing lifecycle. Missing daemon is success for status/stop. Other task operations fail when it is absent.
- `help`, `help COMMAND`, `COMMAND --help`, `COMMAND -h`, `--help`, `-h`, `--version`: no background launch, store lock or state modification. Command help includes syntax, examples, options, absent-background behavior and exit codes. F1 → CLI commands displays the same localized content. Help uses saved EN/RU; `--json` is not supported for help/version.

Trash rechecks the completed entry and its saved target; missing files, directories and symbolic links are rejected without removing the entry. Failures retain the entry. If saving the queue fails after a successful move, the file remains in Trash and the entry may remain in the list: check it, then remove the entry normally. No automatic rollback or repeat occurs. F9 always preserves files.

```sh
deneb help remove
deneb remove TASK_ID --trash --yes   # macOS only, recoverable through system Trash
deneb remove TASK_ID                # remove from list only
```

IDs are full UUIDs (hyphenated or 32 hexadecimal characters), or unique hexadecimal prefixes of 8–32 characters. All IDs resolve against one snapshot before mutation; ambiguity or unknown IDs abort submission. Repeated IDs are deduplicated. A task removed by another client after resolution can appear in `failed`.

## JSON schemaVersion 1

`--json` outputs exactly one JSON object on stdout and no localized text on stderr:

```json
{"schemaVersion":1,"result":{"backgroundRunning":false,"globallyPaused":false,"totalCount":0,"activeCount":0,"jobs":[],"added":[],"processed":[],"skipped":[],"failed":[],"failedInput":null,"notSentInputs":[],"outcomeUnknown":false},"error":null}
```

This is a separate public DTO, not serialized IPC or storage data. `jobs` is populated by list/show. `activeCount` counts all downloading jobs; `totalCount` describes the last queue snapshot, not just filtered jobs. IDs are full UUID strings. `added` contains acknowledged IDs; `failedInput` and `notSentInputs` are 1-based input positions. On disconnect, `outcomeUnknown: true` means the failed input/command may actually have executed: inspect the queue before retrying.

Each job contains `id`, `name`, `state`, `phase`, `bytes`, nullable `totalBytes`, `bytesPerSecond`, nullable `etaSeconds`, `connections`, `connectionLimit`, host-only `source`, nullable `target`, nullable `error`, and `segments`. States are `Queued`, `Downloading`, `Paused`, `Completed`, `NeedsDecision`, `Failed`. Phases are stable enum names (`Queued`, `GlobalPause`, `ManualPause`, `Completed`, `Failed`, `NeedsDecision`, `Probing`, `ProbeRetry`, `Transferring`, `Retrying`, `Ready`, `Stopped`, `Waiting`, `Assembling`, `Verifying`, `DiskPause`). Unknown totals/times are null, never localized strings.

Segments contain `number`, `start`, nullable `end` (inclusive), `bytes`, `complete`, `phase`, `retry`, nullable `retryInSeconds`. An error contains `code` and nullable `httpStatus`; no exception text or historical raw diagnostics. Top-level command codes include `CliInvalid`, `CliDeleteConfirm`, `CliId`, `CliSingleName`, `CliInvalidUrl`, `BatchFailed`, `OutcomeUnknown`, and engine/control diagnostic names. Consumers should handle unknown error codes safely.

Exit codes: **0** success (including allowed skips), **1** execution failure or unknown outcome, **2** invalid input, **3** partial failure with acknowledged successful/skipped items. JSON numbers and enum names do not depend on language. Text output uses saved language and neutralizes terminal control characters in names/paths without modifying stored data.

Use `deneb add --stdin < links.txt` for private links instead of placing tokens in shell history. Treat the input file and JSON paths/names as private data. Deneb never prints the full source URL in these commands; explicit user-provided names/paths are not redacted.

## Совместимость

The additive `result.failures` array contains per-ID diagnostics: `{ "id": "UUID", "error": { "code": "Persistence", "httpStatus": null }, "fileMoved": true }`. `fileMoved` indicates that Trash succeeded but the subsequent queue operation failed; do not repeat the move. Missing files use `MissingFile`, unsafe file types `UnsafeFileType`, unsupported platforms `UnsupportedPlatform`, and system Trash errors `TrashFailed`.

Версия приложения 2.2.0, IPC v4, очередь v5 без миграции. Остановите старый фон старым бинарником. Фильтры/поиск хранятся только в UI-сессии; JSON не зависит от языка. При частичном добавлении ранее добавленные задачи сохраняются; при неизвестном результате сначала проверьте очередь. Публикация сборок — после отдельной приёмки.
