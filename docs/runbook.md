# Runbook

## Запуск (только через явный вызов pwsh)

Расширение `.ps1` в этой системе ассоциировано с `notepad.exe` — запуск вида
`.\run_LLMNetHelper.ps1` НЕ исполняет скрипт, а открывает его в Блокноте.
Всегда:

```powershell
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause <режим>"
```

UTF-8-префикс обязателен: консоль стартует в CP866, кириллица без него ломается.
Для неинтерактивных запусков — всегда `-NoPause`.

## Режимы раннера (взаимоисключающие)

Порядок проверки: `-Test` > `-Check` > `-Info` > `-Stop` > `-Daemon` > `-RunNow` > `-DryRun`.

| Команда | Действие |
|---|---|
| без флага | **-DryRun по умолчанию**: сбор фактов + snapshot без LLM (бесплатно, безопасно) |
| `-RunNow` | Полный прогон с LLM-диагностикой (требует `LLM__ENDPOINT` + `LLM__APIKEY`) |
| `-RunNow -Hours 12` | Своё окно анализа (часов назад) |
| `-Daemon` | Планировщик + health-эндпоинт в фоне (PID-файл и логи в `<ReportsDir>\logs`) |
| `-Daemon -Foreground` | То же в текущем терминале |
| `-Stop` | Остановить демон, запущенный этим скриптом (только свой PID из файла) |
| `-Check` | Проверить доступность Loki и Prometheus |
| `-Test` | `dotnet test` по решению `src/LLMNetHelper/LLMNetHelper.slnx` |
| `-CloneAll` | Скачать/обновить все репозитории группы GitLab в `GitLab:CloneRoot` (включает GitLab только на этот запуск) |
| `-CloneAll -ForceClone` | То же, но существующие клоны удаляются и клонируются заново |
| `-Info` | Показать разрешённую конфигурацию (пути, порты, существование каталогов) |
| `-NoBuild` | Не пересобирать перед запуском |
| `-Roslyn` / `-CodeChange` | Включить соответствующие подсистемы только для этого запуска (переопределяют `.env`) |
| `-NoSemanticKernel` | SK-проекты компилируются пустыми (быстрее, без ~30 пакетов) |

## Коды возврата

| Код | Значение |
|---|---|
| 0 | OK / WARNING (или dry-run успешен) |
| 1 | Сбой сбора фактов или LLM |
| 2 | Вердикт CRITICAL — требуется вмешательство |
| 3 | Прогон уже идёт другим экземпляром |
| 4 | Неверная конфигурация (валидация в раннере и хосте) |

## Где что лежит

- Отчёты: `reports\monitoring\` — `snapshot_*.md`, `monitoring_report_*.md|.json`, `patch_*.diff`.
- База фактов: `reports\monitoring\facts.db` (SQLite, WAL).
- Логи агента: `reports\monitoring\logs\monitor-agent.*.log` + `monitor-agent.pid`
  для демона. Ротация вывода демона — 7 дней.
- Логи НЕ идут в общий каталог: в этом репозитории нет Writer/Stat/Promtail.

## Типовая диагностика

### Агент не стартует в фоне
1. `run_LLMNetHelper.ps1 -Info` — проверить, что health-порт не занят
   (`Test-AgentPort` печатается в выводе).
2. HttpListener на `http://localhost:<port>/` без админ-прав требует
   `netsh http add urlacl url=http://localhost:8091/ user=%USERNAME%` —
   иначе старт даёт `AccessDenied`.
3. Смотреть `reports\monitoring\logs\monitor-agent.stderr.log`.

### Прогон упал с кодом 1
1. `run_LLMNetHelper.ps1 -Check` — Loki/Prometheus доступны?
   Loki сразу после старта контейнера отдаёт 503 («prologue: not ready») —
   это прогрев, `-Check` повторяет до 5 раз.
2. Без источников прогон соберёт пустую сводку — это не падение, но диагноз
   будет бессмысленным.
3. LLM-ошибки (401/таймаут) видны в логе прогона; ключ — только в `.env`.

### SQLite Error 1: no such table: runs
Инициализация хранилища (WAL + миграции) выполняется до первого прогона
в планировщике и на ручном пути (`--run-now`/`--dry-run`). Если ошибка
повторяется — база повреждена; удалить `facts.db` и запустить `-DryRun`.

### Отчёты ушли не в тот каталог
Признак запуска НЕ через раннер (например, прямой `dotnet run` из
произвольного каталога): относительные пути разрешились от рабочего каталога
процесса. Запускать через `run_LLMNetHelper.ps1` или задать абсолютные пути в `.env`.

### Клонирование репозиториев группы GitLab

```powershell
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause -CloneAll"
```

- Требуется `GITLAB__TOKEN` в `src/LLMNetHelper/.env` (права `read_api` + `read_repository`).
- Каталог назначения — `GitLab:CloneRoot` (по умолчанию `repos/GPM`, в `.gitignore`).
- ID группы задавать не нужно: при `GitLab:GroupId = 0` группа ищется по
  `GitLab:GroupName` (полный путь, по умолчанию `GPM/backend`).
- Повторный запуск идемпотентен: новые репозитории клонируются (`--depth 1`),
  существующие обновляются (`fetch --all --prune` + `pull --ff-only`).
  Локальные правки в клонах не сбрасываются — при конфликте операция завершится
  с `FAIL`, а не перезатрёт изменения.
- `-ForceClone` удаляет клоны и клонирует заново (теряет локальные правки).
- Коды возврата: `0` — всё синхронизировано, `1` — есть неудачные операции,
  `4` — неверная конфигурация (пустой токен, группа не найдена, нет прав).
- Особенности, учтённые в коде (меняются через секцию `GitLab`):
  - `TlsProtocols = "Tls12"` — на хостах, где TLS 1.3 в SChannel падает при
    рукопожатии с `gitlab.intellt.ru` (`Win32Exception 0x80004005`);
  - `AllowLongPaths = true` — `core.longpaths` для git-процессов, иначе
    репозитории с путями > 260 символов не распаковываются («File name too long»);
  - `ExcludeArchived = true` — архивные проекты группы не скачиваются. В
    `GPM/backend` их 43 из 103 (в том числе `test_service` → клон
    `repos/GPM/TEST_SERVICE`). Фильтрация идёт серверным параметром
    `archived=false` плюс клиентская страховка по полю `archived`, потому что
    документированный для GitLab `exclude_archived=true` на версии 19.2.9
    **игнорируется**, а запрос с `simple=true` поле `archived` не возвращает
    (поэтому из URL он убран). Вернуть архивные в прогон:
    `GITLAB__EXCLUDEARCHIVED=false`.

### Проверка конфигурации перед включением правок
1. `CODECHANGE__ENABLED=false` — дефолт, не менять «на всякий случай».
2. Включение только флагом `-CodeChange` на конкретный запуск.
3. `RequireCleanWorktree=true` — агент откажет, если дерево грязное; это защита,
   а не баг.
4. Push не выполняется — мерж ветки `fix/ai-*` только вручную после ревью.

## Тесты

Только через `dotnet test` для решения проекта:

```powershell
dotnet test src/LLMNetHelper/LLMNetHelper.slnx
```

Все тесты офлайн: сеть и Docker не требуются. Фейковые `IChatClient`
(ScriptedChatClient) покрывают оркестраторы без реальных вызовов LLM.

### Интеграционные тесты (сеть)

Исключение — тесты с трейтом `Category=Integration`. В обычном прогоне они
имеют статус **Skipped** (не Failed), поэтому офлайн-прогон не ломается.

GitLab-интеграция (`LLMNetHelper.Tests/GitLab/GitLabIntegrationTests.cs`)
проверяет реально: валидность токена (`GET user`), доступность группы
`GPM/backend` (`GET groups?search=`) и список проектов группы
(`GET groups/{id}/projects`). Выполняется только при обоих условиях:

- `GITLAB__INTEGRATION=1` в окружении;
- токен `GITLAB__TOKEN` (из окружения или `src/LLMNetHelper/.env`).

```powershell
$env:GITLAB__INTEGRATION='1'
dotnet test src/LLMNetHelper/LLMNetHelper.slnx --filter Category=Integration
```

Примечание: фикстура фиксирует TLS 1.2 — TLS 1.3 в локальном SChannel при
рукопожатии с `gitlab.intellt.ru` падает с `Win32Exception 0x80004005`
(«Не удается установить связь с локальной системой безопасности»).
