# Архитектура

## Слои проектов

Решение: `src/LLMNetHelper/LLMNetHelper.slnx` (14 проектов).

| Проект | Каталог | Назначение |
|---|---|---|
| `LLMCore.Abstractions` | `src/LLMCore/` | Общие LLM-контракты ядра (`IAnalysisLlmClient`, `LlmRequest`/`LlmCompletion`, `LlmOptions`). Зависимостей нет |
| `LLMNetHelper.Abstractions` | `src/LLMNetHelper/` | Контракты и модели домена мониторинга. Зависимостей нет |
| `LLMNetHelper.Core` | `src/LLMNetHelper/` | Сбор фактов, сводка, анализ, отчётность, хранилище |
| `LLMNetHelper.Context` | `src/LLMNetHelper/` | Источники контекста: обход файлов, чанкинг, BM25-ранжирование |
| `LLMNetHelper.Roslyn` | `src/LLMNetHelper/` | Анализ кода: загрузка решения, AST, поиск, граф вызовов |
| `LLMNetHelper.CodeChange` | `src/LLMNetHelper/` | Git, сборка/тесты, транзакция правки с откатом |
| `LLMNetHelper.Agent` | `src/LLMNetHelper/` | Инструменты, реестр, оркестраторы, промпты |
| `LLMNetHelper.GitLab` | `src/LLMNetHelper/` | GitLab API, резолв группы, клонирование/обновление репозиториев |
| `LLMNetHelper.Infrastructure` | `src/LLMNetHelper/` | HTTP-клиенты Loki/Prometheus, файловый вывод отчётов |
| `LLMNetHelper` | `src/LLMNetHelper/` | Worker Service: хостинг, планировщик, health-эндпоинт |
| `LLMNetHelper.Tests` | `src/LLMNetHelper/` | xUnit, полностью офлайн (кроме `Category=Integration`) |
| `LLMCore.Llm` | `src/LLMCore/` | LLM-транспорт: собственный OpenAI-совместимый клиент + адаптер MEAI |
| `LLMCore.Orchestration.SemanticKernel` | `src/LLMCore/` | Опциональный SK-оркестратор |
| `LLMCore.Orchestration.SemanticKernel.Tests` | `src/LLMCore/` | Паритетные тесты SK-оркестратора |

Зависимости направлены строго вниз: `Agent` → `Core`/`Context`/`Roslyn` →
`Abstractions`; `LLMCore.Llm` → `LLMCore.Abstractions`. Оба `Abstractions`
не знают ни о чём: ядро и продукт разделены контрактами, обратных ссылок
(`LLMCore.*` → `LLMNetHelper.*` в части LLM-контрактов) нет.

## Поток выполнения прогона

```
Loki + Prometheus
      ↓  фиксированные запросы в коде
SnapshotBuilder → компактная сводка фактов
      ↓
IContextSource (docs/) → релевантные документы (BM25)
Roslyn → структура решения (если включён)
      ↓
IAgentOrchestrator (native | semantic-kernel)
      ↓  ReAct: вызовы инструментов из белого списка
Diagnosis { verdict, findings, hypotheses }
      ↓
FixPlan { changes, risks }          (если EnableFixPlanning)
      ↓  (если CodeChange:Enabled)
ChangeTransaction: ветка → AST-правка → build → test → commit
      ↓
Markdown + JSON + patch.diff в Agent:ReportsDir
```

## Ключевые принципы

1. **Детерминированный сбор фактов.** Запросы к Loki и Prometheus зафиксированы
   в коде и версионируются. LLM не формирует их сама — иначе ночной прогон
   невоспроизводим, а расход токенов непредсказуем.
2. **Белый список инструментов.** `ToolRegistry` отклоняет вызов инструмента
   вне реестра до выполнения. Реестр инструментов: поиск по документации,
   поиск по коду, чтение кода, граф вызовов, точки отказа.
3. **Правки через AST, а не через текст.** `AstPatcher` поддерживает только
   `ReplaceMethodBody` и `InsertNullCheck`.
4. **Откат при любом провале.** `ChangeTransaction` возвращает репозиторий
   в исходное состояние, если не прошли сборка или тесты.
5. **Push не выполняется.** Только локальный коммит в ветку `fix/ai-{runId}`.
   Мерж — вручную после проверки человеком.
6. **Безопасные дефолты.** `Roslyn:Enabled=false`, `CodeChange:Enabled=false`.
7. **Доменный контекст — в конфигурации, не в коде.** Предметное описание
   анализируемой системы задаётся опцией `Agent:Orchestration:DomainContext`.
   При пустом значении агент получает нейтральную формулировку и строит
   гипотезы только на фактах сводки и документации. Это позволяет одному
   коду обслуживать разные системы без правки промпта.
8. **Время — только UTC.** Границы окна, дни отчётов, метки времени в SQLite.

## Оркестраторы

`native` (по умолчанию) — собственный ReAct-цикл поверх `IChatClient` из
`Microsoft.Extensions.AI`. `semantic-kernel` — оркестратор на Semantic Kernel;
реализован и покрыт паритетными тестами, но **не подключён** к Worker:
`AddSemanticKernelOrchestrator()` не вызывается в `DependencyInjection`,
поэтому выбор `semantic-kernel` в конфигурации даёт `InvalidOperationException`.

Оба оркестратора используют один и тот же `AgentPromptBuilder`, поэтому
текст промпта не зависит от «движка».

Схема ReAct-цикла по шагам, стратегии (`DiagnosisReActStrategy` /
`ChatReActStrategy`), текстовый протокол вызова инструментов и состав белых
списков (8 инструментов прогона, 6 инструментов чата) — в
[agent-loop.md](agent-loop.md).

## LLM-транспорты

| Провайдер | `IAnalysisLlmClient` | `IChatClient` |
|---|---|---|
| `custom` (по умолчанию) | `CustomOpenAiCompatibleClient` | `CustomLlmChatClient` (адаптер над тем же транспортом) |
| `meai` | алиас `custom`: та же пара классов | та же пара классов |

Исторически `meai` означал отдельный транспорт (`MeaiLlmClient`), удалённый
как неиспользуемый: конфигурационное значение `meai` принимается без
изменения поведения.

`CustomLlmChatClient` **не поддерживает tool calling на уровне протокола**:
оркестратор получает текстовые ответы и разбирает запрос на вызов инструмента
из JSON-ответа модели. Это работает для обоих оркестраторов.

## Хранение

SQLite (`Agent:DatabasePath`), миграции — встроенные SQL-скрипты
(`Core/Storage/Migrations/`). Хранится история прогонов, сигнатуры ошибок
и факты. Глубина — `Agent:RetentionDays` (очистка при прогоне).

Отчёты — файлы в `Agent:ReportsDir`: `snapshot_*.md`,
`monitoring_report_*.md` / `.json`, `patch_*.diff`.
