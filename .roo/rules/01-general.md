# Правила проекта Artas2026

## Timezone: вся логика — по UTC

- **Все вычисления ведутся по UTC.** Границы календарного дня, база интереса дня
  (`day_start_interest` / `FromStartInterest`), счётчики и любые сопоставления дат —
  только по UTC (`DateTime.UtcNow`, `MyTick.TimeUtc`, `DateOnly.FromDateTime(...Utc)`).
- **МСК (UTC+3) используется ИСКЛЮЧИТЕЛЬНО для вывода данных при визуализации**
  (UI, графики, отображение времени). `MyTick.Time = TimeUtc + 3h` — только для
  отображения, НЕ для бизнес-логики.
- Причина: границы дня должны совпадать с UTC-датой хранения (`public.fractals.time`).
  Определение дня по МСК приводило к тому, что первый фрактал нового календарного дня
  получал базу интереса от предыдущего дня (неверный `from_start_interest`).
- Запрещено использовать `TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time")`
  и `MyTick.Time` в расчётах границ дня, баз интереса и счётчиков.

## Логи: смотреть сразу в D:\work\Artas2026\logs

- **При любом упоминании «логов» / «logs» / «посмотри логи» и т.п. сразу читать
  каталог `D:\work\Artas2026\logs`** — не искать его заново и не перебирать
  другие расположения.
- Каталог задаётся переменной `LOGS_DIR` в [`.env`](.env:20)
  (`LOGS_DIR=D:\work\Artas2026\logs`). Скрипты `run_Writer.ps1` и `run_Stat.ps1`
  используют его как единый каталог (в т.ч. для Promtail/Loki).
- Известные файлы:
  - `logs/writer.stdout.log` — основной лог Writer (уровень `info`).
  - `logs/writer.stderr.log` — stderr Writer.
- Кодировка файлов — UTF-8. При чтении через терминал использовать
  `-Encoding UTF8` (иначе кириллица ломается); предпочтительно читать через
  инструмент чтения файлов.

## Запуск .ps1: только через `pwsh -File`, никогда `.\script.ps1`

Терминал Roo стартует в **`cmd.exe`**, а ассоциация `.ps1` в Windows указывает на
`notepad.exe` (`ftype Microsoft.PowerShellScript.1 = "...notepad.exe" "%1"`).
Поэтому `.\script.ps1` не исполняется, а открывается в Блокноте. Любой `.ps1`
запускать **только** через явный вызов интерпретатора PowerShell 7:

```
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & '<путь_к_скрипту>' <параметры>"
```

- `-File`/`&` — обязательно (минует ассоциацию notepad и реально исполняет файл).
- UTF-8-префикс обязателен: процесс PS стартует с `[Console]::OutputEncoding=CP866`,
  иначе кириллица ломается.
- Для неинтерактивных запусков всегда `-NoPause` (иначе терминал «зависнет» на `pause`).
- Путь — относительно корня `d:/work/Artas2026`.

Подробности и примеры — в `.roo/rules-code/02-powershell.md`.

## Процессы: НЕ завершать без явного разрешения пользователя

- **Запрещено** останавливать/убивать любые процессы (Writer, Stat, Reader, Bokeh,
  docker-контейнеры и т.п.) без **явного** разрешения пользователя в текущей сессии.
- Это касается в т.ч. `Stop-Process`, `taskkill`, `/exit`, `kill_by_ports.ps1`,
  `safe_shutdown.ps1`, `stop_all_and_shutdown.ps1`, `docker stop/rm`.
- Диагностика выполняется **только в режиме чтения**: логи, `netstat`, `Get-Process`,
  HTTP-GET-запросы, Event Log, Loki-запросы. Процесс пользователя не трогать.
- Если для проверки требуется перезапуск — сначала **спросить разрешение**, затем
  действовать. Пользователь запускает Writer/Stat сам.
- Причина: в предыдущей сессии Writer был остановлен без согласования; пользователь
  явно запретил убивать процесс без разрешения.

## Tools

You have access to a set of tools to help answer the user's question. You can invoke tools by writing a "<function_calls>" block like the following:
