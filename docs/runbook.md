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

Порядок проверки: `-Test` > `-Check` > `-Info` > `-Stop` > `-CloneAll` > `-Chat` > `-Daemon` > `-RunNow` > `-DryRun`.

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
| `-Chat` | Интерактивный чат с ИИ по скачанным репозиториям (`GitLab:CloneRoot`); требует `LLM__ENDPOINT`/`LLM__APIKEY`, токен GitLab не нужен |
| `-Chat -Ask "вопрос"` | Один вопрос и выход (для скриптов/CI) |
| `-Chat -Repo <имя>` | Начать чат, сузив поиск до одного репозитория `CloneRoot/<имя>` |
| `-Chat -Tools` | Начать чат в агентском режиме (ReAct + инструменты `repo_search`/`repo_read_file`/`repo_list_files`; после `/repo <имя>` и с `-Roslyn` добавляются `repo_failure_points`/`repo_call_graph`/`repo_symbol_search`) |
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

### Чат по скачанным репозиториям

```powershell
# 1. Скачать репозитории группы (однократно, нужен GITLAB__TOKEN)
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause -CloneAll"

# 2. Интерактивный чат (нужны LLM__ENDPOINT + LLM__APIKEY, токен не нужен)
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -Chat"

# Один вопрос и выход (CI/скрипты):
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause -Chat -Ask 'Что делает класс Writer?'"
```

- Команды REPL: `/help`, `/exit`, `/repos` (список репозиториев),
  `/repo <имя>` (сузить поиск; без имени — снова все), `/tools` (переключить
  агентский ReAct-цикл), `/clear` (история), `/status`.
- По умолчанию вопрос → BM25-поиск фрагментов по `GitLab:CloneRoot` → ответ
  модели с цитированием путей. В режиме `/tools` модель сама вызывает
  `repo_search` / `repo_read_file` / `repo_list_files` (белый список,
  guard путей внутри CloneRoot, секреты режутся `Redactor`).
- Roslyn-анализ в чате (план 21): при `-Chat -Tools -Roslyn` и выбранном
  `/repo <имя>` к белому списку добавляются `repo_failure_points`
  (пустые/нелогирующие catch, throw null), `repo_call_graph` (область влияния
  правки) и `repo_symbol_search` (точные координаты объявлений). Работают
  только для выбранного репозитория: файл решения ищется в его каталоге
  (`*.slnx` → `*.sln` → единственный `*.csproj`). Требуют полный MSBuild SDK
  на хосте; в Docker — недоступны. Доступность показывает `/status`.
- **Hosted-сервисы стартуют всегда** (наблюдаемость): health-эндпоинт
  `http://localhost:<Agent:HealthPort>/health` доступен всё время чата,
  ночной планировщик работает. Поэтому порт health не должен быть занят
  демоном — иначе раннер откажет (код 4): остановите демона `-Stop` или
  задайте другой `AGENT__HEALTHPORT`.
- Настройки поиска/лимитов — секция `Chat` (см. [`docs/configuration.md`](configuration.md)).
- **Транскрипт диалога**: весь сеанс пишется в один Markdown-файл
  `logs\dialogs\chat-<yyyy-MM-dd_HH-mm-ssZ>.md` (время старта — UTC) —
  вопросы, команды, шаги ReAct-цикла с полными результатами инструментов,
  полный текст BM25-фрагментов контекста (отключается
  `CHAT__DIALOGLOG__INCLUDECONTEXTFRAGMENTS=false`), финальные ответы,
  лимиты/сбои; токены каждого ответа модели (вход/выход) и суммарные за
  сеанс — в футере. Путь — переменная
  `CHAT__DIALOGLOG__DIRECTORY` (раннер нормализует относительный путь от корня
  репозитория), отключение — `CHAT__DIALOGLOG__ENABLED=false`. Каталог
  `logs/*` в `.gitignore`. Подробная таблица параметров —
  [`docs/configuration.md`](configuration.md#транскрипт-диалога-chatdialoglog).
- Коды возврата: `0` — сеанс завершён, `1` — сбой LLM/IO/хоста,
  `4` — конфигурация (пустые креды LLM, нет CloneRoot/репозитория, занят порт).

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

### Интеграционные тесты чата

`LLMNetHelper.Tests/Chat/ChatIntegrationTests.cs` проверяет режим `-Chat`
**реальным** вызовом LLM по сети: временный `CloneRoot` с фикстурами →
BM25-поиск `ChatScope` → промпт → настоящий транспорт
`CustomOpenAiCompatibleClient`/`CustomLlmChatClient` (через DI-регистрацию
`AddMonitorAgentLlm`) → ответ `ChatSession`.

Что проверяется:

1. разрешение кредитов из окружения / `.env`;
2. режим A (`-Ask`): ответ содержит маркер из фикстуры — сквозная проверка
   поиска, транспорта и промпта;
3. сужение `/repo other-repo`: маркер из другого репозитория не утекает;
4. режим B (`/tools`): ReAct-цикл с реальными `repo_search`/`repo_read_file`
   приходит к тому же маркеру.

Гейт — `CHAT__INTEGRATION=1`; креды — `LLM__ENDPOINT`/`LLM__APIKEY`
(из окружения или `src/LLMNetHelper/.env`, тот же парсер, что у GitLab).
Без гейта тесты имеют статус **Skipped** — офлайн-прогон не ломается.

```powershell
$env:CHAT__INTEGRATION='1'
dotnet test src/LLMNetHelper/LLMNetHelper.slnx --filter Category=Integration
```

Примечания:

- Фикстура фиксирует TLS 1.2 для именованного HttpClient `llm` — та же
  особенность хоста, что у GitLab-интеграции (TLS 1.3 в SChannel падает при
  рукопожатии).
- Креды разрешаются в порядке: переменная окружения → `src/LLMNetHelper/.env` →
  `Llm:*` из `appsettings.json` — то есть тест работает с той же конфигурацией,
  что и реальный запуск (`LLM__ENDPOINT` может быть не секретом и лежать в
  `appsettings.json`, ключ — в `.env`).
- Интеграционный тест выявил и починил дефект транспорта:
  `CustomOpenAiCompatibleClient` безусловно парсил `ExpectedJsonSchema` —
  при пустой схеме (свободный ответ чата) запрос падал с
  «The input does not contain any JSON tokens». Теперь при пустой схеме
  `response_format` не отправляется вовсе.
