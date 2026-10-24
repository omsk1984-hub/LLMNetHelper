# Конфигурация

## Источники конфигурации и приоритеты

1. **Переменные окружения процесса** — задаются до запуска (флаги раннера
   `-Roslyn` / `-CodeChange` ставят переменные ДО загрузки `.env` и потому
   выигрывают у файла).
2. **`src/LLMNetHelper/.env`** — локальный файл агента (в `.gitignore`).
   Парсер примитивный: `KEY=VALUE`, инлайн-комментарии НЕ срезаются,
   кавычки обрезаются, уже заданная переменная НЕ перезаписывается.
3. **`appsettings.json`** рядом с сборкой — факультативно; если есть,
   переменные окружения перекрывают его (порядок провайдеров в `Program.cs`:
   JSON → окружение).
4. **Дефолты классов опций** — последний слой.

Единственный шаблон конфигурации в репозитории —
`src/LLMNetHelper/.env.example`. Копирование:
`Copy-Item src/LLMNetHelper/.env.example src/LLMNetHelper/.env`.

## Маппинг имён

Двойное подчёркивание = разделитель секции:

| Переменная | Секция:ключ |
|---|---|
| `LLM__PROVIDER` | `Llm:Provider` |
| `AGENT__HEALTHPORT` | `Agent:HealthPort` |
| `Loki__BaseUrl` | `Loki:BaseUrl` |
| `AGENT__ORCHESTRATION__DOMAINCONTEXT` | `Agent:Orchestration:DomainContext` |
| `Context__Sources__local-folder__Path` | `Context:Sources:local-folder:Path` |

Имя секции содержит дефис (`local-folder`) — в имени переменной дефис
сохраняется как есть, нижнее подчёркивание его НЕ заменяет.

Массивы — индексом после `__`: `CONTEXT__SOURCES__LOCAL_FOLDER__INCLUDE__0=**/*.md`.

## Пути: относительные разрешаются от корня репозитория

При запуске через `run_LLMNetHelper.ps1` относительные каталожные пути
нормализуются от корня репозитория и передаются процессу абсолютными.
Это касается: `AGENT__REPORTSDIR`, `AGENT__DATABASEPATH`,
`Context__Sources__local-folder__Path`, `ROSLYN__SOLUTIONPATH`,
`CODECHANGE__REPOSITORYPATH`, `CODECHANGE__SOLUTIONPATH`.

При запуске напрямую `dotnet run` без раннера относительные пути
разрешаются от рабочего каталога процесса — указывайте абсолютные.

## Agent (расписание и хранение)

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Agent:RunAtUtc` | `AGENT__RUNATUTC` | `03:00` | Время ночного запуска (UTC, `HH:mm`) |
| `Agent:WindowHours` | `AGENT__WINDOWHOURS` | `24` | Глубина окна анализа в часах |
| `Agent:ReportsDir` | `AGENT__REPORTSDIR` | `reports\monitoring` | Каталог отчётов |
| `Agent:DatabasePath` | `AGENT__DATABASEPATH` | `reports\monitoring\facts.db` | SQLite-база фактов |
| `Agent:RetentionDays` | `AGENT__RETENTIONDAYS` | `90` | Дней хранения истории прогонов |
| `Agent:TopErrorsLimit` | `AGENT__TOPERRORSLIMIT` | `25` | Уникальных ошибок в сводке |
| `Agent:MaxSampleChars` | `AGENT__MAXSAMPLECHARS` | `2000` | Длина одного сэмпла ошибки |
| `Agent:HealthPort` | `AGENT__HEALTHPORT` | `8080` (код) / `8091` (.env) | Порт `/health` и `/status` |

Health-порт должен быть свободен. `HttpListener` на `http://localhost:<port>/`
без прав администратора требует резервирования URL:
`netsh http add urlacl url=http://localhost:8091/ user=%USERNAME%`.

## Оркестратор (`Agent:Orchestration:*`)

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Orchestrator` | `AGENT__ORCHESTRATION__ORCHESTRATOR` | `native` | `native` или `semantic-kernel` (второй не подключён к хосту) |
| `MaxSteps` | `AGENT__ORCHESTRATION__MAXSTEPS` | `8` | Максимум итераций ReAct |
| `ToolCallLimit` | `AGENT__ORCHESTRATION__TOOLCALLLIMIT` | `30` | Максимум вызовов инструментов |
| `EnableFixPlanning` | `AGENT__ORCHESTRATION__ENABLEFIXPLANNING` | `true` | Строить план правки |
| `DomainContext` | `AGENT__ORCHESTRATION__DOMAINCONTEXT` | пусто | Предметное описание системы для промпта; пусто — нейтральный текст, гипотезы только по фактам |

## LLM (`Llm:*`)

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Provider` | `LLM__PROVIDER` | `custom` | `custom` (свой HttpClient) или `meai` (алиас `custom`: единый транспорт, см. `docs/architecture.md`) |
| `Endpoint` | `LLM__ENDPOINT` | — | Корень OpenAI-совместимого API; клиент сам постит на `/v1/chat/completions` |
| `ApiKey` | `LLM__APIKEY` | пусто | СЕКРЕТ — только в `.env`. Пусто → заголовок Authorization не добавляется, `-RunNow` упадёт на 401 |
| `Model` | `LLM__MODEL` | `qwen3.8-omni-flash` | Модель |
| `MaxTokens` | `LLM__MAXTOKENS` | `4000` | Лимит генерации |
| `Temperature` | `LLM__TEMPERATURE` | `0.1` | Температура |
| `TimeoutSeconds` | `LLM__TIMEOUTSECONDS` | `120` | Таймаут запроса |
| `MaxRetries` | `LLM__MAXRETRIES` | `3` | Ретраи транспорта |

## Источники фактов

| Параметр | Env | Дефолт (хост) | Описание |
|---|---|---|---|
| `Loki:BaseUrl` | `Loki__BaseUrl` | `http://localhost:3100` | Loki; в Docker — `http://loki:3100` |
| `Loki:TimeoutSeconds` | `Loki__TimeoutSeconds` | `30` | Таймаут |
| `Loki:MaxEntries` | `Loki__MaxEntries` | `500` | Лимит записей на запрос |
| `Prometheus:BaseUrl` | `Prometheus__BaseUrl` | `http://localhost:9090` | Prometheus; в Docker — `http://prometheus:9090` |
| `Prometheus:TimeoutSeconds` | `Prometheus__TimeoutSeconds` | `30` | Таймаут |

Запросы к обоим источникам зафиксированы в коде и версионируются —
конфигурируются только адреса, таймауты и лимиты.

## Контекст (`Context:*`)

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Enabled` | `CONTEXT__ENABLED` | `true` | Сбор контекста включён |
| `TopK` | `CONTEXT__TOPK` | `8` | Документов на запрос |
| `MaxChars` | `CONTEXT__MAXCHARS` | `6000` | Символов на документ |
| `TotalCharsBudget` | `CONTEXT__TOTALCHARSBUDGET` | `24000` | Бюджет всего контекста |
| `Sources:local-folder:Path` | `Context__Sources__local-folder__Path` | `docs` | Каталог документации |
| `Sources:local-folder:Include` | `CONTEXT__SOURCES__LOCAL_FOLDER__INCLUDE__0` | `**/*.md` | Что читать |
| `Sources:local-folder:MaxFiles` | `Context__Sources__local-folder__MaxFiles` | `2000` | Лимит файлов |
| `Sources:local-folder:MaxFileBytes` | `Context__Sources__local-folder__MaxFileBytes` | `524288` | Лимит размера файла |
| `Sources:local-folder:ChunkSize` | `Context__Sources__local-folder__ChunkSize` | `3000` | Размер чанка |
| `Sources:local-folder:ChunkOverlap` | `Context__Sources__local-folder__ChunkOverlap` | `300` | Перекрытие чанков |

Добавить новый источник (Confluence, Jira, pgvector) — реализовать
`IContextSource` и добавить `services.AddContextSource<T>()` в
`DependencyInjection.cs` хост-проекта. Прикладной код не меняется.

## Roslyn (`Roslyn:*`) — анализ кода

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Enabled` | `ROSLYN__ENABLED` | `false` | Безопасный дефолт. Включение раннером: `-Roslyn` |
| `SolutionPath` | `ROSLYN__SOLUTIONPATH` | `src\LLMNetHelper\LLMNetHelper.slnx` | Решение/проект для MSBuildWorkspace |
| `CompilationCacheSeconds` | `ROSLYN__COMPILATIONCACHESECONDS` | `300` | Кэш компиляции |
| `MaxSourceChars` | `ROSLYN__MAXSOURCECHARS` | `20000` | Лимит исходника на чтение |
| `MaxSearchResults` | `ROSLYN__MAXSEARCHRESULTS` | `50` | Лимит результатов поиска |

Загрузка решения через MSBuildWorkspace стоит десятков секунд и требует
полного MSBuild SDK — в Docker-образе недоступна.

## CodeChange (`CodeChange:*`) — применение правок

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Enabled` | `CODECHANGE__ENABLED` | `false` | **Разрешает агенту менять код.** Включение раннером: `-CodeChange` |
| `RepositoryPath` | `CODECHANGE__REPOSITORYPATH` | `.` | Корень git-репозитория |
| `BaseBranch` | `CODECHANGE__BASEBRANCH` | `main` | Базовая ветка (в этом репозитории — `main`) |
| `RequireCleanWorktree` | `CODECHANGE__REQUIRECLEANWORKTREE` | `true` | Отказ при грязном дереве |
| `BuildTimeoutSeconds` | `CODECHANGE__BUILDTIMEOUTSECONDS` | `600` | Таймаут сборки |
| `TestTimeoutSeconds` | `CODECHANGE__TESTTIMEOUTSECONDS` | `900` | Таймаут тестов |
| `SkipTests` | `CODECHANGE__SKIPTESTS` | `false` | Пропускать тесты |
| `SolutionPath` | `CODECHANGE__SOLUTIONPATH` | `src\LLMNetHelper\LLMNetHelper.slnx` | Решение для build/test |
| `GitExecutable` | `CODECHANGE__GITEXECUTABLE` | `git` | Путь к git |
| `DotnetExecutable` | `CODECHANGE__DOTNETEXECUTABLE` | `dotnet` | Путь к dotnet |
| `BranchPrefix` | `CODECHANGE__BRANCHPREFIX` | `fix/ai-` | Префикс веток правок |

Агент создаёт ветку `fix/ai-{runId}`, применяет правку через AST, прогоняет
`dotnet build` + `dotnet test`, коммитит при успехе. **Push не выполняется.**
Любой провал → полный откат репозитория.

## Чат (`Chat:*`) — режим `-Chat`

Консольный REPL-чат с LLM по вопросам из каталога скачанных репозиториев
(`GitLab:CloneRoot`, по умолчанию `repos/GPM`). Запуск:
`run_LLMNetHelper.ps1 -Chat` (подробности — [`docs/runbook.md`](runbook.md)).

Hosted-сервисы (`HealthServer` + планировщик) в режиме чата стартуют
**всегда** — наблюдаемость сохраняется, health-порт должен быть свободен.

| Параметр | Env | Дефолт | Описание |
|---|---|---|---|
| `Include` | `CHAT__INCLUDE__0` | `**/*.md`, `**/*.cs`, `**/*.csproj`, … | Glob-шаблоны файлов для поиска (код + документы) |
| `TopK` | `CHAT__TOPK` | `8` | Фрагментов контекста на один вопрос |
| `MaxChars` | `CHAT__MAXCHARS` | `6000` | Символов на один фрагмент |
| `TotalCharsBudget` | `CHAT__TOTALCHARSBUDGET` | `24000` | Бюджет символов контекста на вопрос |
| `MaxFiles` | `CHAT__MAXFILES` | `20000` | Лимит файлов за один обход CloneRoot |
| `MaxFileBytes` | `CHAT__MAXFILEBYTES` | `524288` | Лимит размера файла |
| `ChunkSize` / `ChunkOverlap` | `CHAT__CHUNKSIZE` / `CHAT__CHUNKOVERLAP` | `3000` / `300` | Нарезка длинных файлов |
| `HistoryMessages` | `CHAT__HISTORYMESSAGES` | `20` | Сообщений диалога в истории |
| `MaxSteps` | `CHAT__MAXSTEPS` | `8` | Лимит итераций ReAct (режим `/tools`) |
| `ToolCallLimit` | `CHAT__TOOLCALLLIMIT` | `30` | Лимит вызовов инструментов за вопрос |
| `ListLimit` | `CHAT__LISTLIMIT` | `200` | Лимит `repo_list_files` |
| `DialogLog.Enabled` | `CHAT__DIALOGLOG__ENABLED` | `true` | Писать ли Markdown-транскрипт диалога |
| `DialogLog.Directory` | `CHAT__DIALOGLOG__DIRECTORY` | `logs/dialogs` | Каталог транскриптов (относительный путь раннер нормализует от корня репозитория) |
| `DialogLog.IncludeContextFragments` | `CHAT__DIALOGLOG__INCLUDECONTEXTFRAGMENTS` | `true` | Полный текст BM25-фрагментов контекста в транскрипте (`false` — только аннотации) |

Исключения (`bin/obj/node_modules/.git/.env/*.key/…`) наследуются дефолтами
`LocalFolderOptions.Exclude`. Секреты в выдаче инструментов режутся `Redactor`.
Токен GitLab (`GITLAB__TOKEN`) для чата **не требуется** — работа идёт с
локальными копиями; обязательны `LLM__ENDPOINT` + `LLM__APIKEY`.

### Транскрипт диалога (`Chat:DialogLog`)

Каждый сеанс чата пишется в **один Markdown-файл** от запуска до
`/exit`/EOF: `logs/dialogs/chat-{yyyy-MM-dd_HH-mm-ssZ}.md` (время старта —
**UTC**; при коллизии имени — суффиксы `-2`, `-3`, …). Фиксируется всё:

- вопросы пользователя и команды (`/repo`, `/tools`, `/clear`, `/exit`, …);
- в режиме `/tools` — каждый шаг ReAct-цикла: сырой ответ модели
  (JSON-запрос инструмента), имя и аргументы инструмента, **полный текст**
  результата, финальный ответ;
- в режиме BM25 — вопрос, найденные фрагменты контекста и ответ; по умолчанию
  пишется **полный текст** каждого фрагмента (ровно то, что видела модель,
  `DialogLog.IncludeContextFragments=true`); при `false` — только аннотации
  (заголовок, score, длина, путь);
- пометки о лимитах, сбоях и смене области/режима;
- футер с итогами сеанса (вопросов/команд, вызовов инструментов).

Файл — UTF-8 без BOM, flush после каждой записи (читаем в процессе сеанса).
Сбой записи (диск/права) **не рвёт чат**: транскрипт отключается с
предупреждением в Serilog. Каталог `logs/*` в `.gitignore` — транскрипты
не попадают в репозиторий. Реализация:
`LLMNetHelper/Chat/ChatDialogLogger.cs` (`IChatTranscript`,
`ChatDialogLogger`, `NullChatTranscript`); план — `plans/20_chat-dialog-logging-plan.md`.

Интеграционный тест чата (реальный вызов LLM по сети) включается гейтом
`CHAT__INTEGRATION=1` и использует те же `LLM__ENDPOINT`/`LLM__APIKEY`;
по умолчанию тесты имеют статус **Skipped** (см. [`docs/runbook.md`](runbook.md)).
