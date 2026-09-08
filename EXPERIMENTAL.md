# Experimental Windows / Linux builds · Экспериментальные сборки

These are **Deneb 2.2.0 experimental platform builds**, not stable Windows/Linux releases. The application version is 2.2.0; these archives are prepared for acceptance, not yet published. Stable macOS releases remain separate.

- Windows x64 and Linux x64 (glibc) only. Linux builds are tested on Ubuntu in CI; Alpine/musl and ARM64 are not included.
- The .NET runtime is bundled. Linux still needs the OS libraries required by .NET 10 (including ICU, OpenSSL and zlib).
- Automated core, IPC and packaged CLI checks are not full manual terminal acceptance. Keyboard mappings, rendering and terminal behavior may vary.
- Opening files, revealing them in a file manager and copying paths are currently macOS-only; these actions report an unsupported-platform message elsewhere.
- Extract the entire archive before running. Linux: `./deneb`; Windows PowerShell: `.\deneb.exe`. Use a terminal at least 120×35 (Windows Terminal recommended).
- Try a separate queue: `./deneb --state-dir ./deneb-test-state` or `.\deneb.exe --state-dir .\deneb-test-state`.
- Q / Ctrl+C closes only the interface. Use F10 or `deneb stop` (with the same `--state-dir`) to stop background downloads. No login autostart or sleep prevention.
- Stop any old Deneb process before upgrading. Do not open a migrated v5 queue with Deneb 2.0 or earlier. These binaries are unsigned; verify the attached SHA-256 rather than disabling system security.
- Report issues at https://github.com/yazovskiy/Deneb/issues with OS, terminal and reproduction steps. Never post private URLs, tokens or state.json.

## По-русски

Это **экспериментальные сборки Deneb 2.2.0 для Windows/Linux**, не стабильный релиз для этих платформ. Версия приложения — 2.2.0; архивы подготовлены для приёмки, ещё не опубликованы.

Поддерживаются только x64: Windows и Linux с glibc. .NET включён, но Linux требует системные библиотеки runtime (включая ICU, OpenSSL и zlib). ARM64 и Alpine/musl не включены. Автотесты не заменяют ручную приёмку терминального интерфейса; клавиши и отрисовка могут зависеть от терминала. Открытие файла, показ в файловом менеджере и копирование пути пока доступны только на macOS.

Распакуйте архив полностью, запустите `./deneb` в Linux или `.\deneb.exe` в PowerShell. Рекомендуется терминал 120×35; для Windows — Windows Terminal. Для пробы задайте отдельный `--state-dir`. Q закрывает только интерфейс; полная остановка — F10 или `deneb stop` с тем же каталогом состояния. Перед обновлением остановите старый процесс. Не открывайте очередь v5 в Deneb 2.0 и более ранних версиях. Бинарники не подписаны: проверяйте SHA-256, не отключайте защиту системы целиком.
