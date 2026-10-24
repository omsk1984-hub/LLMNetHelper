# LLMNetHelper — Nightly LLM Agent: мониторинг с анализом кода

Ночной агент проверки Loki и Prometheus, который диагностирует инциденты
с привлечением документации проекта и анализа C#-кода, а при необходимости —
предлагает и применяет правку в отдельной ветке.

Дополнительно — интерактивный режим `-Chat`: консольный чат с ИИ по вопросам
из каталога скачанных репозиториев GitLab (`GitLab:CloneRoot`, см. `-CloneAll`).

Репозиторий **автономен**: собственное решение, своя конфигурация, никаких
зависимостей от других репозиториев. Относительные пути в `.env` разрешаются
от корня репозитория. Подробная документация — в [`docs/`](docs/README.md):
[архитектура](docs/architecture.md), [конфигурация](docs/configuration.md),
[runbook](docs/runbook.md), [развёртывание](docs/deployment.md).

## Архитектура

```
LLMNetHelper.Abstractions              — контракты домена, без зависимостей
LLMNetHelper.Core                      — сборка фактов, сводка, отчётность
LLMNetHelper.Context                   — источники контекста (FileWalker, BM25)
LLMNetHelper.Roslyn                    — анализ кода (SolutionLoader, AST, патчи)
LLMNetHelper.CodeChange                — git + build/test + транзакция с откатом
LLMNetHelper.Agent                     — инструменты, оркестратор, промпты
LLMNetHelper.GitLab                    — GitLab API + клонирование группы репозиториев
LLMNetHelper.Infrastructure            — HTTP-клиенты, файловый I/O
LLMNetHelper                           — Worker Service (хостинг + планировщик)
LLMNetHelper.Tests                     — xUnit, офлайн
```

**Ядро LLM** (в каталоге `src/LLMCore/`; SK-часть отключается флагом `-p:EnableSemanticKernel=false`):

```
LLMCore.Abstractions                       — общие LLM-контракты (IAnalysisLlmClient, LlmRequest/LlmCompletion, LlmOptions)
LLMCore.Llm                                — LLM-транспорт (собственный OpenAI-совместимый + адаптер MEAI)
LLMCore.Orchestration.SemanticKernel       — SK 1.81.0 оркестратор
LLMCore.Orchestration.SemanticKernel.Tests — паритетные тесты
```

Все проекты входят в решение [`src/LLMNetHelper/LLMNetHelper.slnx`](src/LLMNetHelper/LLMNetHelper.slnx:1).
При `EnableSemanticKernel=false` SK-проекты компилируются пустыми (`Compile Remove`),
поэтому `dotnet build` решения остаётся зелёным.

### Поток выполнения

```
Loki + Prometheus
      ↓ (фиксированные запросы в коде)
   SnapshotBuilder → компактная сводка
      ↓
IContextSource (docs/) → релевантные документы (BM25)
Roslyn → структура решения
      ↓
IAgentOrchestrator (native | semantic-kernel)
      ↓ ReAct: инструменты из белого списка
  Diagnosis { verdict, findings, hypotheses }
      ↓
  FixPlan { changes, risks }
      ↓ (если CodeChange:Enabled=true)
ChangeTransaction: ветка → AST-правка → build → test → commit
      ↓
   Markdown + JSON + patch.diff
```

## Запуск

Кратко; полностью — [`docs/runbook.md`](docs/runbook.md).

```powershell
# .env из шаблона
Copy-Item src/LLMNetHelper/.env.example src/LLMNetHelper/.env

# Сборка и тесты
dotnet build src/LLMNetHelper/LLMNetHelper.slnx -c Release
dotnet test  src/LLMNetHelper/LLMNetHelper.slnx

# Запуск раннера (.ps1 ассоциирован с notepad — вызывать pwsh явно):
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -NoPause"

# Чат по скачанным репозиториям (сначала -CloneAll; нужны LLM__ENDPOINT/LLM__APIKEY):
pwsh -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; chcp 65001 > $null; & 'src/LLMNetHelper/run_LLMNetHelper.ps1' -Chat"
```

## Ключевые принципы

1. **Детерминированный сбор фактов.** Запросы к Loki и Prometheus зафиксированы
   в коде и версионируются. LLM не формирует их сам — иначе ночной прогон
   невоспроизводим, а расход токенов непредсказуем.
2. **Белый список инструментов.** [`ToolRegistry`](LLMNetHelper.Agent/Tools/ToolRegistry.cs)
   отклоняет вызов инструмента вне реестра до выполнения.
3. **Правки через AST, а не текст.** [`AstPatcher`](LLMNetHelper.Roslyn/AstPatcher.cs)
   поддерживает только `ReplaceMethodBody` и `InsertNullCheck`.
4. **Откат при любом провале.** [`ChangeTransaction`](LLMNetHelper.CodeChange/ChangeTransaction.cs)
   возвращает репозиторий в исходное состояние, если не прошла сборка или тесты.
5. **Push не выполняется.** Только локальный коммит в ветку `fix/ai-{runId}`.
   Мерж — вручную после проверки.
6. **Безопасные дефолты.** `CodeChange:Enabled=false` и `Roslyn:Enabled=false`.
7. **Всё по UTC.** Границы дня, окна анализа и метки времени — только UTC.
8. **Доменный контекст — в конфигурации.** Предметное описание анализируемой
   системы задаётся опцией `Agent:Orchestration:DomainContext`
   (`AGENT__ORCHESTRATION__DOMAINCONTEXT`), а не зашито в промпт: при пустом
   значении агент строит гипотезы только на фактах сводки и документации.

## Конфигурация

Основной источник — переменные окружения из `src/LLMNetHelper/.env`
(шаблон [`src/LLMNetHelper/.env.example`](src/LLMNetHelper/.env.example:1));
`appsettings.json` факультативен
(пример — [`LLMNetHelper/appsettings.example.json`](LLMNetHelper/appsettings.example.json:1)).
Полная справка — [`docs/configuration.md`](docs/configuration.md).

### Основное

| Параметр | Env | По умолчанию | Описание |
|---|---|---|---|
| `Agent:RunAtUtc` | `AGENT__RUNATUTC` | `03:00` | Время запуска (UTC) |
| `Agent:WindowHours` | `AGENT__WINDOWHOURS` | `24` | Окно анализа |
| `Agent:ReportsDir` | `AGENT__REPORTSDIR` | `./reports/monitoring` | Каталог отчётов |
| `Llm:Provider` | `LLM__PROVIDER` | `custom` | `custom` или `meai` |

### Оркестратор

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Agent:Orchestration:Orchestrator` | `native` | `native` или `semantic-kernel` |
| `Agent:Orchestration:MaxSteps` | `8` | Максимум шагов ReAct-цикла |
| `Agent:Orchestration:ToolCallLimit` | `30` | Максимум вызовов инструментов |
| `Agent:Orchestration:EnableFixPlanning` | `true` | Строить план правки |
| `Agent:Orchestration:DomainContext` | пусто | Описание системы для промпта; пусто — нейтральный текст |

### Источники контекста

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Context:Enabled` | `true` | Включён ли сбор контекста |
| `Context:Sources:local-folder:Path` | `docs` | Каталог документации |
| `Context:Sources:local-folder:Include` | `["**/*.md"]` | Что читать |
| `Context:TopK` | `8` | Документов на запрос |
| `Context:TotalCharsBudget` | `24000` | Общий бюджет символов |

**Добавить новый источник** (Confluence, Jira, pgvector) — реализовать
[`IContextSource`](LLMNetHelper.Abstractions/IContextSource.cs:1)
и добавить одну строку `services.AddContextSource<T>()` в
[`DependencyInjection`](LLMNetHelper/DependencyInjection.cs:1).
Прикладной код не меняется.

### Анализ кода и правки

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Roslyn:Enabled` | `false` | Анализ кода (нужен полный MSBuild SDK) |
| `Roslyn:SolutionPath` | — | Путь к `.slnx` / `.sln` / `.csproj` |
| `CodeChange:Enabled` | `false` | **Разрешить агенту менять код** |
| `CodeChange:RepositoryPath` | — | Корень git-репозитория |
| `CodeChange:RequireCleanWorktree` | `true` | Отказ при грязном дереве |

### Чат (`Chat:*`, режим `-Chat`)

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Chat:Include` | код + документы (glob) | Шаблоны файлов CloneRoot для поиска |
| `Chat:TopK` | `8` | Фрагментов контекста на вопрос |
| `Chat:HistoryMessages` | `20` | Сообщений диалога в истории |
| `Chat:MaxSteps` / `Chat:ToolCallLimit` | `8` / `30` | Лимиты агентского режима `/tools` |

Поиск — BM25 по `GitLab:CloneRoot` (переиспользует слой контекста);
`/repo <имя>` сужает область до одного репозитория; `/tools` включает
ReAct-цикл с инструментами `repo_search` / `repo_read_file` / `repo_list_files`
(белый список + guard путей + `Redactor`). Hosted-сервисы (health/планировщик)
в режиме чата стартуют всегда. Подробно — [`docs/configuration.md`](docs/configuration.md)
и [`docs/runbook.md`](docs/runbook.md).

## Провайдеры LLM и Semantic Kernel

Версия `Microsoft.Extensions.AI` зафиксирована на **10.5.0** во всех проектах —
она совпадает с требованием `Microsoft.SemanticKernel.Connectors` 1.81.0.

### `IChatClient` и `IAnalysisLlmClient`

Оба контракта регистрируются всегда, независимо от `Llm:Provider`:

| Провайдер | `IAnalysisLlmClient` | `IChatClient` |
|---|---|---|
| `custom` (по умолчанию) | [`CustomOpenAiCompatibleClient`](src/LLMCore/LLMCore.Llm/CustomOpenAiCompatibleClient.cs:1) | [`CustomLlmChatClient`](src/LLMCore/LLMCore.Llm/CustomLlmChatClient.cs:1) — адаптер над тем же транспортом |
| `meai` | алиас `custom` — та же пара классов | та же пара классов |

Контракты живут в [`LLMCore.Abstractions`](src/LLMCore/LLMCore.Abstractions/IAnalysisLlmClient.cs:1):
ядро LLM не зависит от продукта (`LLMNetHelper.*`).

`CustomLlmChatClient` **не поддерживает tool calling на уровне протокола**:
оркестратор получает текстовые ответы и разбирает запрос на вызов инструмента
из JSON-ответа модели. Это работает для обоих оркестраторов.

### Включение SK-оркестратора

Проект `Orchestration.SemanticKernel` собирается по умолчанию.
Отключить (быстрее, без ~30 пакетов SK):

```bash
dotnet build src/LLMNetHelper/LLMNetHelper.slnx -p:EnableSemanticKernel=false
```

Выбор в рантайме:

```json
{ "Agent": { "Orchestration": { "Orchestrator": "semantic-kernel" } } }
```

**Важно:** `AddSemanticKernelOrchestrator()` пока **не вызывается** в
[`DependencyInjection`](LLMNetHelper/DependencyInjection.cs:1) —
выбор `semantic-kernel` в конфиге приведёт к `InvalidOperationException`
с объяснением. Оркестратор реализован и покрыт тестами, но не подключён
к Worker; подключение требует регистрации `IChatClient` в SK-ядре (см. §«Ограничения»
в [`IMPLEMENTATION_REPORT.md`](IMPLEMENTATION_REPORT.md:1)).

Паритет поведения с нативным оркестратором проверяется тестами
`SemanticKernelOrchestratorTests` (8 тестов).

## Тесты

```bash
# Всё решение целиком
dotnet test src/LLMNetHelper/LLMNetHelper.slnx

# Только агент (офлайн, без Docker)
dotnet test src/LLMNetHelper/LLMNetHelper.Tests/LLMNetHelper.Tests.csproj

# Только паритет SK-оркестратора
dotnet test src/LLMCore/LLMCore.Orchestration.SemanticKernel.Tests/LLMCore.Orchestration.SemanticKernel.Tests.csproj

# Интеграционные тесты GitLab (требуют сети и GITLAB__TOKEN)
$env:GITLAB__INTEGRATION='1'
dotnet test src/LLMNetHelper/LLMNetHelper.slnx --filter Category=Integration
```

Все тесты, кроме помеченных `Category=Integration`, офлайн — сеть и Docker не
требуются; интеграционные по умолчанию имеют статус **Skipped**. Актуальное
количество показывает `dotnet test` решения; через раннер: `run_LLMNetHelper.ps1 -Test`.

## Коды возврата

| Код | Значение |
|---|---|
| 0 | OK / WARNING |
| 1 | Сбой сбора или LLM |
| 2 | Вердикт CRITICAL |
| 3 | Прогон уже идёт |
| 4 | Неверная конфигурация |
