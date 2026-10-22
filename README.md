# Nightly LLM Agent — мониторинг с анализом кода

Ночной агент проверки Loki и Prometheus, который диагностирует инциденты
с привлечением документации проекта и анализа C#-кода, а при необходимости —
предлагает и применяет правку в отдельной ветке.

## Архитектура

```
ArtasTools.MonitorAgent.Abstractions              — контракты, без зависимостей
ArtasTools.MonitorAgent.Core                      — сборка фактов, сводка, отчётность
ArtasTools.MonitorAgent.Context                   — источники контекста (FileWalker, BM25)
ArtasTools.MonitorAgent.Roslyn                    — анализ кода (SolutionLoader, AST, патчи)
ArtasTools.MonitorAgent.CodeChange                — git + build/test + транзакция с откатом
ArtasTools.MonitorAgent.Agent                     — инструменты, оркестратор, промпты
ArtasTools.MonitorAgent.Llm                       — LLM-транспорты (custom + MEAI)
ArtasTools.MonitorAgent.Infrastructure            — HTTP-клиенты, файловый I/O
ArtasTools.MonitorAgent                           — Worker Service (хостинг + планировщик)
ArtasTools.MonitorAgent.Tests                     — xUnit, офлайн
```

**Опционально** (отключается флагом `-p:EnableSemanticKernel=false`):

```
ArtasTools.MonitorAgent.Orchestration.SemanticKernel        — SK 1.81.0 оркестратор
ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests  — паритетные тесты
```

Все 13 проектов входят в решение [`src/Tools/ArtasTools.slnx`](../../src/Tools/ArtasTools.slnx:1).
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

## Ключевые принципы

1. **Детерминированный сбор фактов.** Запросы к Loki и Prometheus зафиксированы
   в коде и версионируются. LLM не формирует их сам — иначе ночной прогон
   невоспроизводим, а расход токенов непредсказуем.
2. **Белый список инструментов.** [`ToolRegistry`](ArtasTools.MonitorAgent.Agent/Tools/ToolRegistry.cs)
   отклоняет вызов инструмента вне реестра до выполнения.
3. **Правки через AST, а не текст.** [`AstPatcher`](ArtasTools.MonitorAgent.Roslyn/AstPatcher.cs)
   поддерживает только `ReplaceMethodBody` и `InsertNullCheck`.
4. **Откат при любом провале.** [`ChangeTransaction`](ArtasTools.MonitorAgent.CodeChange/ChangeTransaction.cs)
   возвращает репозиторий в исходное состояние, если не прошла сборка или тесты.
5. **Push не выполняется.** Только локальный коммит в ветку `fix/ai-{runId}`.
   Мерж — вручную после проверки.
6. **Безопасные дефолты.** `CodeChange:Enabled=false` и `Roslyn:Enabled=false`.
7. **Всё по UTC.** Границы дня, окна анализа и метки времени — только UTC.

## Запуск

```bash
# Только анализ и отчёт (без правок)
dotnet run --project ArtasTools.MonitorAgent -- --run-now

# Сбор фактов без вызова LLM
dotnet run --project ArtasTools.MonitorAgent -- --dry-run

# Своё окно анализа
dotnet run --project ArtasTools.MonitorAgent -- --run-now --hours 12
```

Docker и управление — в [`deploy/monitor-agent/`](../../deploy/monitor-agent).

## Конфигурация

Полный пример — [`ArtasTools.MonitorAgent/appsettings.example.json`](ArtasTools.MonitorAgent/appsettings.example.json:1).

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

### Источники контекста

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Context:Enabled` | `true` | Включён ли сбор контекста |
| `Context:Sources:local-folder:Path` | `docs` | Каталог документации |
| `Context:Sources:local-folder:Include` | `["**/*.md"]` | Что читать |
| `Context:TopK` | `8` | Документов на запрос |
| `Context:TotalCharsBudget` | `24000` | Общий бюджет символов |

**Добавить новый источник** (Confluence, Jira, pgvector) — реализовать
[`IContextSource`](ArtasTools.MonitorAgent.Abstractions/IContextSource.cs:1)
и добавить одну строку `services.AddContextSource<T>()` в
[`DependencyInjection`](ArtasTools.MonitorAgent/DependencyInjection.cs:1).
Прикладной код не меняется.

### Анализ кода и правки

| Параметр | По умолчанию | Описание |
|---|---|---|
| `Roslyn:Enabled` | `false` | Анализ кода (нужен полный MSBuild SDK) |
| `Roslyn:SolutionPath` | — | Путь к `.slnx` / `.sln` / `.csproj` |
| `CodeChange:Enabled` | `false` | **Разрешить агенту менять код** |
| `CodeChange:RepositoryPath` | — | Корень git-репозитория |
| `CodeChange:RequireCleanWorktree` | `true` | Отказ при грязном дереве |

## Провайдеры LLM и Semantic Kernel

Версия `Microsoft.Extensions.AI` зафиксирована на **10.5.0** во всех проектах —
она совпадает с требованием `Microsoft.SemanticKernel.Connectors` 1.81.0.

### `IChatClient` и `IAnalysisLlmClient`

Оба контракта регистрируются всегда, независимо от `Llm:Provider`:

| Провайдер | `IAnalysisLlmClient` | `IChatClient` |
|---|---|---|
| `custom` (по умолчанию) | [`CustomOpenAiCompatibleClient`](ArtasTools.MonitorAgent.Llm/CustomOpenAiCompatibleClient.cs:1) | [`CustomLlmChatClient`](ArtasTools.MonitorAgent.Llm/CustomLlmChatClient.cs:1) — адаптер над тем же транспортом |
| `meai` | [`MeaiLlmClient`](ArtasTools.MonitorAgent.Llm/MeaiLlmClient.cs:1) | тот же транспорт с семантикой MEAI 10.5.0 |

`CustomLlmChatClient` **не поддерживает tool calling на уровне протокола**:
оркестратор получает текстовые ответы и разбирает запрос на вызов инструмента
из JSON-ответа модели. Это работает для обоих оркестраторов.

### Включение SK-оркестратора

Проект `Orchestration.SemanticKernel` собирается по умолчанию.
Отключить (быстрее, без ~30 пакетов SK):

```bash
dotnet build src/Tools/ArtasTools.slnx -p:EnableSemanticKernel=false
```

Выбор в рантайме:

```json
{ "Agent": { "Orchestration": { "Orchestrator": "semantic-kernel" } } }
```

**Важно:** `AddSemanticKernelOrchestrator()` пока **не вызывается** в
[`DependencyInjection`](ArtasTools.MonitorAgent/DependencyInjection.cs:1) —
выбор `semantic-kernel` в конфиге приведёт к `InvalidOperationException`
с объяснением. Оркестратор реализован и покрыт тестами, но не подключён
к Worker; подключение требует регистрации `IChatClient` в SK-ядре (см. §«Ограничения»
в [`IMPLEMENTATION_REPORT.md`](IMPLEMENTATION_REPORT.md:1)).

Паритет поведения с нативным оркестратором проверяется тестами
`SemanticKernelOrchestratorTests` (8 тестов).

## Тесты

```bash
# Всё решение целиком (191 тест: 145 + 8 + 20 + 18)
dotnet test src/Tools/ArtasTools.slnx

# Только агент (офлайн, без Docker)
dotnet test ArtasTools.MonitorAgent.Tests/ArtasTools.MonitorAgent.Tests.csproj

# Только паритет SK-оркестратора
dotnet test ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests/ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests.csproj
```

Покрытие: **145** основных + **8** SK-паритетных тестов.
Все офлайн — сеть и Docker не требуются.

## Коды возврата

| Код | Значение |
|---|---|
| 0 | OK / WARNING |
| 1 | Сбой сбора или LLM |
| 2 | Вердикт CRITICAL |
| 3 | Прогон уже идёт |
| 4 | Неверная конфигурация |
