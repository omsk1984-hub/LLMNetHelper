# Архитектура

## Слои проектов

Решение: `src/LLMMetrics/LLMNetHelper.slnx` (12 проектов).

| Проект | Назначение |
|---|---|
| `ArtasTools.MonitorAgent.Abstractions` | Контракты и модели. Зависимостей нет |
| `ArtasTools.MonitorAgent.Core` | Сбор фактов, сводка, анализ, отчётность, хранилище |
| `ArtasTools.MonitorAgent.Context` | Источники контекста: обход файлов, чанкинг, BM25-ранжирование |
| `ArtasTools.MonitorAgent.Roslyn` | Анализ кода: загрузка решения, AST, поиск, граф вызовов |
| `ArtasTools.MonitorAgent.CodeChange` | Git, сборка/тесты, транзакция правки с откатом |
| `ArtasTools.MonitorAgent.Agent` | Инструменты, реестр, оркестраторы, промпты |
| `ArtasTools.MonitorAgent.Llm` | LLM-транспорты: `custom` и `meai` |
| `ArtasTools.MonitorAgent.Infrastructure` | HTTP-клиенты Loki/Prometheus, файловый вывод отчётов |
| `ArtasTools.MonitorAgent` | Worker Service: хостинг, планировщик, health-эндпоинт |
| `ArtasTools.MonitorAgent.Tests` | xUnit, полностью офлайн |
| `ArtasTools.MonitorAgent.Orchestration.SemanticKernel` | Опциональный SK-оркестратор |
| `ArtasTools.MonitorAgent.Orchestration.SemanticKernel.Tests` | Паритетные тесты SK-оркестратора |

Зависимости направлены строго вниз: `Agent` → `Core`/`Context`/`Roslyn` →
`Abstractions`. `Abstractions` не знает ни о чём.

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

## LLM-транспорты

| Провайдер | `IAnalysisLlmClient` | `IChatClient` |
|---|---|---|
| `custom` (по умолчанию) | `CustomOpenAiCompatibleClient` | `CustomLlmChatClient` (адаптер над тем же транспортом) |
| `meai` | `MeaiLlmClient` | тот же транспорт MEAI 10.5.0 |

`CustomLlmChatClient` **не поддерживает tool calling на уровне протокола**:
оркестратор получает текстовые ответы и разбирает запрос на вызов инструмента
из JSON-ответа модели. Это работает для обоих оркестраторов.

## Хранение

SQLite (`Agent:DatabasePath`), миграции — встроенные SQL-скрипты
(`Core/Storage/Migrations/`). Хранится история прогонов, сигнатуры ошибок
и факты. Глубина — `Agent:RetentionDays` (очистка при прогоне).

Отчёты — файлы в `Agent:ReportsDir`: `snapshot_*.md`,
`monitoring_report_*.md` / `.json`, `patch_*.diff`.
