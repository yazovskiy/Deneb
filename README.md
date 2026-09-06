<p align="center">
  <img src="docs/assets/deneb-banner.svg" alt="Deneb — downloads, under control" width="900">
</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest"><img alt="Release" src="https://img.shields.io/github/v/release/yazovskiy/Deneb?color=8b9cff&style=flat-square"></a>
  <img alt="macOS Apple Silicon" src="https://img.shields.io/badge/macOS-Apple%20Silicon-111827?style=flat-square&logo=apple">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-7dd3fc?style=flat-square"></a>
</p>

<p align="center"><b>Полноэкранный менеджер загрузок для вашего терминала.</b><br>
Очередь, параллельные соединения и безопасная докачка — без браузера и фоновой службы.</p>

<p align="center">
  <a href="https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz"><b>↓ Скачать для macOS Apple Silicon</b></a>
  &nbsp; · &nbsp; <a href="docs/usage.ru.md">Руководство</a>
  &nbsp; · &nbsp; <a href="CHANGELOG.md">Что нового</a>
</p>

## Быстрый старт

1. Скачайте [готовый архив](https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz) и распакуйте его.
2. Откройте Terminal в распакованной папке и выполните `./deneb`.
3. Нажмите **A**, вставьте ссылки и подтвердите добавление. **F1** покажет все клавиши.

**Устанавливать .NET не нужно:** runtime включён в сборку. Поддерживается macOS на Apple Silicon (ARM64); Intel-сборки пока нет. Для интерфейса рекомендуется терминал не меньше 120×35 символов.

Скачать через терминал:

```sh
mkdir -p deneb-download && cd deneb-download
curl -fL -O https://github.com/yazovskiy/Deneb/releases/latest/download/deneb-osx-arm64.tar.gz
curl -fL -O https://github.com/yazovskiy/Deneb/releases/latest/download/SHA256SUMS.txt
shasum -a 256 -c SHA256SUMS.txt
tar -xzf deneb-osx-arm64.tar.gz
./deneb
```

Сборка не имеет подписи Developer ID и не нотарифицирована Apple. Если macOS блокирует запуск, проверьте источник и контрольную сумму, затем разрешите именно этот файл в системных настройках безопасности. Не отключайте защиту macOS целиком.

## Что умеет Deneb

| Возможность | Как это работает |
|---|---|
| **Очередь под контролем** | Меняйте порядок, назначайте следующую задачу и применяйте действия к отмеченным строкам. |
| **Пауза без сюрпризов** | Общая пауза не отменяет ручные паузы. Порядок и состояние сохраняются после перезапуска. |
| **Параллельная загрузка** | По умолчанию два файла одновременно и до четырёх соединений на файл. |
| **Безопасная докачка** | Проверка Range и валидаторов HTTP; изменившийся файл не дописывается к старым данным. |
| **Живые подробности** | Прогресс сегментов, повторы, обратный отсчёт, сглаженная скорость и оставшееся время. |
| **Готовый файл под рукой** | Открытие, показ в Finder, копирование пути и очистка списка без удаления файлов. |
| **Простое добавление** | Прямые HTTP/HTTPS URL, Markdown-ссылки и строки Android Download Manager. |

Импорт Android переносит **только имя и URL**, не чужой прогресс. Полные ссылки с токенами не выводятся в таблице и подробностях, но хранятся локально для докачки.

## Клавиши

| Клавиша | Действие |
|---|---|
| ↑ / ↓ · `Insert` | Выбрать строку · отметить её |
| `A` · `U` | Добавить ссылки · заменить URL |
| `Space` · `F6` | Пауза/продолжение выбранных · общая пауза |
| `F3` / `F4` · `F5` | Выше/ниже · скачать следующей |
| `Enter` · `F2` | Живые подробности · настройки |
| `O` · `F7` · `F8` | Открыть файл · Finder · скопировать путь |
| `Delete` · `F9` | Убрать выбранные · очистить завершённые |
| `F1` · `Q` / `Ctrl+C` | Справка · корректный выход |

На клавиатуре Mac для F-клавиш может потребоваться **Fn**. Если нет Insert, настройте соответствующую клавишу в терминале.

## Ваши файлы остаются вашими

- Загрузки по умолчанию: `~/Downloads/Deneb`.
- Очередь и настройки: `~/Library/Application Support/Deneb`.
- Существующие файлы не перезаписываются. Удаление задачи по умолчанию сохраняет части; их удаление требует отдельного подтверждения.
- При миграции с 1.0 сохраняется `state.json.v1.bak`. Закройте старую версию перед запуском новой.
- Для сборки сегментов временно требуется место примерно под два размера файла.

Только HTTP/HTTPS. Нет фоновой службы, торрентов, поиска ссылок на сайтах, ADB-интеграции и ручных Cookie/Authorization. Приложение работает, пока открыт процесс.

## Разработка

Требуется SDK **.NET 10.0.300** (или совместимый patch согласно `global.json`).

```sh
git clone https://github.com/yazovskiy/Deneb.git
cd Deneb
dotnet restore Deneb.slnx --locked-mode
dotnet test Deneb.slnx --no-restore
dotnet run --project src/Deneb.App
```

Ядро `Deneb.Core` не зависит от интерфейса. `Deneb.App` использует Terminal.Gui; тесты xUnit работают с локальным HTTP-сервером и детерминированными файлами.

Для версии 1.1.0 проходят **36 автоматических тестов** и PTY-сценарий: очередь, пауза, перезапуск, SHA-256 и сохранение файлов после очистки. Системные действия macOS покрыты тестами адаптера; ручная проверка описана в [руководстве](docs/usage.ru.md).

Сборка релизного архива на Mac ARM64:

```sh
dotnet publish src/Deneb.App -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -o artifacts/osx-arm64
bash scripts/package-release.sh
```

## Обратная связь и лицензия

Нашли проблему? [Создайте issue](https://github.com/yazovskiy/Deneb/issues/new/choose), указав версию, шаги воспроизведения и текст ошибки. **Не публикуйте приватные URL, токены или `state.json`.**

Deneb распространяется под [MIT](LICENSE). Зависимости сохраняют свои лицензии — см. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
