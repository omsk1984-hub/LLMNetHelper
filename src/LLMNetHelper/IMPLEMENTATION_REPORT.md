# LLM Agent — отчёт о реализации

Реализация по планам [`plans/5_agent-sk-roslyn-rebuild-plan.md`](../../plans/5_agent-sk-roslyn-rebuild-plan.md:1)
(перестройка агента) и [`plans/6_meai-migration-10.5-plan.md`](../../plans/6_meai-migration-10.5-plan.md:1)
(миграция Microsoft.Extensions.AI).

**Состояние:** сборка 0 ошибок, **145 основных + 8 SK-паритетных тестов зелёные**.

---

## 1. Что изменилось относительно прежней версии

Прежняя версия была **конвейером**: `collect → snapshot → LLM(один вызов) → report`.
LLM не имел инструментов, не видел код, не мог ничего проверить.

Новая версия — **агент**: собирает факты, ищет документацию, анализирует код через
Roslyn, ставит диагноз с гипотезами, при необходимости предлагает и применяет
правку в отдельной ветке с проверкой сборкой и тестами.

| Аспект | Было | Стало |
|---|---|---|
| Понимание кода | нет | Roslyn: поиск символов, граф вызовов, проблемные места |
| Внешний контекст | нет | `IContextSource` + BM25-ранжирование |
| Диагностика | один вызов LLM | ReAct-цикл с инструментами, гипотезы |
| Правки | нет | AST-патчи + git + build/test + откат |
| Оркестраторы | один | 2 (native / semantic-kernel) с паритетным тестом |
| MEAI | 9.0.0-preview | **10.5.0** |

---

## 2. Структура

```
src/LLMNetHelper/
├── LLMNetHelper.Abstractions/         контракты
│   ├── IContextSource.cs                          источник контекста
│   ├── IAgentOrchestrator.cs                      оркестратор + IToolRegistry
│   ├── IAgentTool.cs                              инструмент агента
│   ├── AgentModels.cs                             Diagnosis, FixPlan, Hypothesis, AgentContext
│   ├── AgentReport.cs                             агентские данные для отчёта
│   ├── ContextDocument.cs                         ContextQuery, ContextDocument, ContextSearchResult
│   └── IFactStore.cs · Models.cs · LlmRequest.cs · *Options.cs
│
├── LLMNetHelper.Core/                 сбор фактов и отчётность
│   ├── Collection/                                Loki и Prometheus (фиксированные запросы)
│   ├── Snapshot/                                  сводка, бюджет токенов, редактор секретов
│   ├── Analysis/                                  промпт, JSON-схема, парсер ответа
│   ├── Reporting/                                 Markdown, JSON, **PatchRenderer**
│   └── Storage/                                   SQLite + миграции 001, 002
│
├── LLMNetHelper.Context/              источники контекста
│   ├── FileWalker.cs                              обход + glob
│   ├── TextChunker.cs                             разбиение по заголовкам
│   ├── KeywordRanker.cs                           BM25 со стеммингом
│   ├── ContextAggregator.cs                       опрос источников, изоляция сбоев
│   └── ContextQueryBuilder.cs                     из фактов → запрос
│
├── LLMNetHelper.Roslyn/               анализ кода
│   ├── SolutionLoader.cs                          MSBuildWorkspace + кэш
│   ├── CodeSearchService.cs · CodeReader.cs       поиск и чтение
│   ├── CallGraphService.cs                        callers / callees
│   ├── FailurePointAnalyzer.cs                    catch/throw проблемы
│   ├── StructureAnalyzer.cs                       структура + DI-регистрации
│   └── AstPatcher.cs · CompilationValidator.cs    правка и проверка
│
├── LLMNetHelper.CodeChange/           применение правок
│   ├── GitService.cs                              git CLI (без LibGit2Sharp)
│   ├── ProcessRunner.cs                           таймауты, kill только своего дерева
│   ├── BuildTestRunner.cs                         dotnet build / test
│   └── ChangeTransaction.cs                       транзакция с откатом
│
├── LLMNetHelper.Agent/                агентская логика
│   ├── Tools/                                     docs_search, code_* (5 инструментов)
│   ├── Orchestration/NativeAgentOrchestrator.cs    ReAct на MEAI
│   └── Orchestration/AgentPromptBuilder.cs         системный промпт
│
├── LLMNetHelper.Llm/                  LLM-транспорты
│   ├── CustomOpenAiCompatibleClient.cs             свой HttpClient
│   ├── MeaiLlmClient.cs                            IChatClient 10.5.0
│   ├── CustomLlmChatClient.cs                      адаптер IChatClient над custom
│   └── LlmClientRegistration.cs                    выбор провайдера
│
├── LLMNetHelper.Infrastructure/       I/O
│   ├── Loki/ · Prometheus/ · Reporting/
│   └── Context/LocalFolderContextSource.cs         файловый источник
│
├── LLMNetHelper/                      Worker Service
│   ├── AgentRunner.cs                              прогон через DI
│   ├── DependencyInjection.cs                      регистрация всех слоёв
│   ├── Program.cs                                  --run-now / --dry-run / --hours
│   └── Scheduling/ · Health/
│
└── LLMNetHelper.Orchestration.SemanticKernel/       SK 1.81.0
    ├── SkPluginAdapter.cs                          IAgentTool → KernelFunction
    └── SemanticKernelAgentOrchestrator.cs
```

---

## 3. Ключевые решения

### 3.1 Гибридный оркестратор

`IAgentOrchestrator` + две реализации, выбор конфигом `Agent:Orchestration:Orchestrator`:

| Реализация | Движок | Когда |
|---|---|---|
| `NativeAgentOrchestrator` | собственный ReAct на MEAI 10.5.0 | по умолчанию |
| `SemanticKernelAgentOrchestrator` | Semantic Kernel 1.81.0 | при явном выборе |

Паритет проверяется 8 тестами: одинаковый ответ модели → эквивалентный `Diagnosis`.

### 3.2 Инструменты — белый список

`ToolRegistry` отклоняет вызов инструмента вне реестра **до выполнения**.
Покрыто тестом, включая попытку вызвать `rm_rf`.

Доступно 5 инструментов:

| Инструмент | Назначение |
|---|---|
| `docs_search` | поиск по всем `IContextSource` |
| `code_search_symbol` | типы/методы по подстроке имени |
| `code_read_member` | исходник метода с координатами |
| `code_call_graph` | callers и callees |
| `code_failure_points` | пустые catch, catch(Exception), throw null |

### 3.3 Правки — только AST

`AstPatcher` поддерживает ровно две операции:
- `ReplaceMethodBody` — замена тела с сохранением trivia;
- `InsertNullCheck` — вставка проверки в начало тела.

Правка сигнатур, переименования и новые файлы намеренно не поддерживаются.

### 3.4 Безопасность

- `CodeChange:Enabled = false` по умолчанию;
- `RequireCleanWorktree = true` — отказ при грязном дереве;
- провал сборки или тестов → полный откат ветки;
- push не выполняется, мерж вручную;
- `Process.Kill` применяется только к собственному дочернему дереву `dotnet`.
- `Redactor` применяется к каждому документу контекста до отправки в LLM.

### 3.5 Миграция MEAI 10.5.0

Контракт изменён: `CompleteAsync`/`ChatCompletion` → `GetResponseAsync`/`ChatResponse`.
`MeaiLlmClient` и `NativeAgentOrchestrator` переведены. Версии совпали с требованием
SK 1.81.0, что позволило реализовать фазу 6.

---

## 4. Исправленные дефекты

Найдены при прогоне тестов и исправлены:

| Дефект | Файл | Суть |
|---|---|---|
| Невалидный режим SQLite | `SqliteFactStore` | `Mode=RWCached` не существует в `SqliteOpenMode` |
| Отсутствовал нативный провайдер SQLite | `SqliteFactStore`, `Core.csproj` | не было `SQLitePCLRaw.bundle_e_sqlite3` и `Batteries_V2.Init()` |
| Retention удалял не то | `SqliteFactStore` | `datetime('now','-0 days')` — начало суток, а не «сейчас»; заменено на UTC-границу из .NET + каскадное удаление |
| Ошибочный using в production | `NightlyScheduleHostedService` | `Microsoft.Extensions.Time.Testing` |
| `PatchRenderer` возвращал заголовки без изменений | `PatchRenderer` | пустой диф отдавал `---/+++` вместо пустой строки |
| `throw null!` не детектировался | `FailurePointAnalyzer` | это `SuppressNullableWarningExpression`, а не литерал `null` |

---

## 5. Тесты

| Группа | Файл | Покрытие |
|---|---|---|
| Контекст | `Context/FileWalkerTests`, `TextChunkerTests`, `KeywordRankerTests`, `LocalFolderContextSourceTests` | обход, glob, чанки, BM25, редакция секретов |
| Roslyn | `Roslyn/FailurePointAnalyzerTests`, `CodeAnalysisTests` | анализ на реальном MSBuild-проекте-фикстуре |
| Правки | `CodeChange/ChangeTransactionTests` | временный git-репо, откат, отсутствие push |
| Агент | `Agent/NativeAgentOrchestratorTests` | ReAct-цикл, лимиты, белый список |
| Отчёты | `Reporting/PatchRendererTests` | unified diff, хунки, пустые диффы |
| Хранилище | `Storage/SqliteFactStoreTests` | миграции, upsert, retention |
| SK-паритет | `Orchestration.SemanticKernel.Tests` | эквивалентность двух оркестраторов |

---

## 6. Ограничения

| Ограничение | Причина | Митигация |
|---|---|---|
| `NU1605` подавлен в `Directory.Build.props` | конфликт `System.CodeDom` 9.0.0 ↔ 7.0.0 в цепочке Roslyn → MSBuild; пакета 9.0.0 нет в офлайн-кэше | зависимость только build-time, в рантайм не попадает |
| SDK-транспорт MEAI не подключён напрямую | имя пакета коннектора совпадает по префиксу с внутренним плейсхолдером среды разработки | используется `CustomLlmChatClient`; точка расширения задокументирована |
| `Roslyn:Enabled = false` по умолчанию | `MSBuildWorkspace` требует полного SDK | агент работает без анализа кода, инструменты сообщают о недоступности |
| Векторного поиска нет | Qdrant/Chroma отсутствуют в кэше | BM25 со стеммингом покрывает потребность документации репозитория |

---

## 7. Команды

```bash
# Сборка
dotnet build LLMNetHelper.Tests/LLMNetHelper.Tests.csproj

# Основные тесты (офлайн)
dotnet test LLMNetHelper.Tests/LLMNetHelper.Tests.csproj

# SK-паритетные тесты
dotnet test LLMNetHelper.Orchestration.SemanticKernel.Tests/LLMNetHelper.Orchestration.SemanticKernel.Tests.csproj

# Сборка без SK (быстрее)
dotnet build -p:EnableSemanticKernel=false

# Ручной запуск
dotnet run --project LLMNetHelper -- --run-now
dotnet run --project LLMNetHelper -- --dry-run
dotnet run --project LLMNetHelper -- --run-now --hours 12
```

---

## 8. Коды возврата

| Код | Значение |
|---|---|
| 0 | OK / WARNING |
| 1 | Сбой сбора или LLM |
| 2 | Вердикт CRITICAL |
| 3 | Прогон уже идёт |
| 4 | Неверная конфигурация |
