# Отчёт: продолжать ли проект LLMNetHelper или взять готовую альтернативу

> Запрос: «лёгкий агент, который чётко выполняет правила: один раз за ночь скачать
> последнюю версию из репозитория, проверить Loki и Prometheus на наличие ошибок,
> подготовить план исправления, исправить их, скомпилировать проект, прогнать тесты
> и составить отчёт».
>
> Дата отчёта: 2026-10-22 (UTC). Обновлённая версия.

---

## Короткий ответ

Готового «коробочного» решения, которое делает ровно этот сценарий (ночной прогон →
сбор фактов из **своих** Loki/Prometheus → диагноз → правка кода в **вашем**
репозитории → build/test → отчёт), **не существует**. Есть либо универсальные
coding-агенты (мониторинг и правила проекта придётся «прикручивать» самому), либо
SaaS/CI-инструменты, которые не закрывают всю цепочку.

Проект [`LLMNetHelper`](../README.md) реализует **100% цепочки**. Продолжать
**стоит** — конвейер полный.

---

## 1. Соответствие требованиям

| Требование | Статус | Где |
|---|---|---|
| Один раз за ночь | ✅ готово: планировщик на `Agent:RunAtUtc` (по умолчанию 03:00 UTC) + catch-up, если прогона не было 24 ч | [`NightlyScheduleHostedService`](../src/LLMNetHelper/LLMNetHelper/Scheduling/NightlyScheduleHostedService.cs) |
| Скачать последнюю версию из репозитория | ✅ готово: `FetchAsync` + `PullHardAsync` вызываются перед прогоном агента | [`GitService`](../src/LLMNetHelper/LLMNetHelper.CodeChange/GitService.cs) |
| Проверить Loki и Prometheus на ошибки | ✅ готово: детерминированные фиксированные запросы, сводка, бюджет токенов, редактура секретов | [`LokiFactCollector`](../src/LLMNetHelper/LLMNetHelper.Core/Collection/LokiFactCollector.cs), [`PrometheusFactCollector`](../src/LLMNetHelper/LLMNetHelper.Core/Collection/PrometheusFactCollector.cs), [`SnapshotBuilder`](../src/LLMNetHelper/LLMNetHelper.Core/Snapshot/SnapshotBuilder.cs) |
| План исправления | ✅ готово: `FixPlan { changes, risks }` при `EnableFixPlanning=true` | [`NativeAgentOrchestrator`](../src/LLMNetHelper/LLMNetHelper.Agent/Orchestration/NativeAgentOrchestrator.cs) |
| Исправить ошибки | ⚠️ частично: только 2 AST-операции (`ReplaceMethodBody`, `InsertNullCheck`), по умолчанию выключено, без push | [`AstPatcher`](../src/LLMNetHelper/LLMNetHelper.Roslyn/AstPatcher.cs), [`ChangeTransaction`](../src/LLMNetHelper/LLMNetHelper.CodeChange/ChangeTransaction.cs) |
| Компиляция + тесты | ✅ готово: `dotnet build` / `dotnet test` с таймаутами, откат при любом провале | [`BuildTestRunner`](../src/LLMNetHelper/LLMNetHelper.CodeChange/BuildTestRunner.cs) |
| Отчёт | ✅ готово: Markdown + JSON + `patch.diff` в `Agent:ReportsDir` | [`MarkdownReportRenderer`](../src/LLMNetHelper/LLMNetHelper.Core/Reporting/MarkdownReportRenderer.cs), [`FileReportWriter`](../src/LLMNetHelper/LLMNetHelper.Infrastructure/Reporting/FileReportWriter.cs) |

Цепочка полная: `fetch → pull (catch-up) → Loki/Prometheus → диагноз → план → AST-правка → build/test → отчёт`.

---

## 2. Готовые альтернативы и почему они не закрывают задачу целиком

```mermaid
flowchart LR
    A[1. git fetch + pull] --> B["2. Сбор фактов<br/>Loki + Prometheus"]
    B --> C[3. Диагноз + план]
    C --> D[4. Правка кода]
    D --> E[5. build + test]
    E --> F[6. Отчёт]

    style A fill:#2e7d32,color:#fff
    style B fill:#2e7d32,color:#fff
    style C fill:#2e7d32,color:#fff
    style D fill:#2e7d32,color:#fff
    style E fill:#2e7d32,color:#fff
    style F fill:#2e7d32,color:#fff
```

**Зелёное — всё уже есть в проекте.**

| Альтернатива | Что умеет | Почему не заменяет проект |
|---|---|---|
| **OpenHands** (headless-режим) | Универсальный агент: читает код, правит, запускает тесты в песочнице | Нет сбора фактов из Loki/Prometheus — нужен внешний «клей»; правила проекта придётся поддерживать промптами; тяжёлый (Docker, Python) |
| **Aider** (CLI, скриптуется из cron/Task Scheduler) | Правки по тексту, авто-коммит, запуск тестов по команде | Нет ни мониторинга, ни планировщика, ни гарантий безопасности (правки текстовые, не AST; нет белого списка инструментов и автоотката) |
| **Claude Code / Codex CLI / Gemini CLI** в headless-режиме (`-p`) | Самые сильные сегодня coding-агенты; правила через `CLAUDE.md`/`AGENTS.md` | Проприетарные, требуют ключей/подписки; «чёткость правил» мягкая (промпт, а не код); мониторинг опять же внешний скрипт |
| **GitHub Copilot coding agent / Codex cloud** | По назначенному issue делает PR | Триггер — issue, а не расписание + состояние вашего Loki; не работает с self-hosted Loki/Prometheus |
| **CI-cron** (GitHub Actions `schedule`, GitLab CI) | Идеален для пункта «раз в ночь» | Это только планировщик; вся логика (факты, LLM, правки) всё равно ваша |
| **Средства observability** (Grafana Sift/Assistant, Datadog Bits AI) | Анализ инцидентов по метрикам/логам | SaaS, не правят код, не знают ваш репозиторий |
| **Фреймворки оркестрации** (LangGraph, CrewAI, AutoGen, Semantic Kernel Process) | Строительные блоки для агентов | Это то же самое, что уже пишется в проекте, но **без** гарантий: белый список [`ToolRegistry`](../src/LLMNetHelper/LLMNetHelper.Agent/Tools/ToolRegistry.cs), AST-only правки, откат [`ChangeTransaction`](../src/LLMNetHelper/LLMNetHelper.CodeChange/ChangeTransaction.cs), отсутствие push. Начинать с них — получить текущий проект минус безопасность |
| **Детерминированная авторемедиация** (StackStorm, Rundeck) | «Если ошибка X — выполни runbook Y», без LLM | Хорошее дополнение, но не строит планов и не правит код |

Вывод: комбинация «ночной детерминированный сбор фактов → агент с белым списком
инструментов → безопасная транзакционная правка → отчёт» — нишевая. Любой
универсальный агент потребует того же объёма обвязки, что уже написана.

---

## 3. Плюсы и минусы продолжения проекта

### Плюсы

1. **100% цепочки реализовано и протестировано**: 153 теста (145 основных + 8 SK-паритетных) зелёные (см. [`IMPLEMENTATION_REPORT.md`](../src/LLMNetHelper/IMPLEMENTATION_REPORT.md)).
2. **«Чёткие правила» — это код, а не промпт**: фиксированные запросы к Loki/Prometheus (LLM не может их выдумать), белый список инструментов в [`ToolRegistry`](../src/LLMNetHelper/LLMNetHelper.Agent/Tools/ToolRegistry.cs), только 2 AST-операции, откат при любом провале, никакого push. У универсальных агентов правила — это мягкий промпт, здесь — жёсткий код.
3. **Детерминизм и воспроизводимость ночного прогона** + предсказуемый расход токенов (принцип №1 в [`docs/architecture.md`](../docs/architecture.md)).
4. **Автономность**: офлайн-кэш NuGet, свои правила в
   [`.roo/rules/01-general.md`](../.roo/rules/01-general.md), всё по UTC —
   ни один внешний агент эти ограничения «из коробки» не уважает.
5. **Безопасные дефолты**: `CodeChange:Enabled=false`, `Roslyn:Enabled=false` —
   агент не сломает репозиторий, пока это явно не разрешено.

### Минусы и риски

1. **Проект не «лёгкий»**: 12 csproj, Roslyn/MSBuild (хрупкий, требует полного
   SDK, поэтому `Roslyn:Enabled=false` по умолчанию), связка версий
   MEAI 10.5.0 ↔ SK 1.81.0. Если цель — лёгкость, отключать SK-сборку
   (`-p:EnableSemanticKernel=false`, см. [`README.md`](../README.md)) и держать
   Roslyn выключенным.
2. **Стоимость сопровождения**: самостоятельно поддерживаются оркестратор, парсер
   ответов модели, миграции SQLite — в то время как открытые агенты (OpenHands,
   Aider) быстро развиваются сообществом.
3. **Ограниченная сила исправления**: 2 AST-операции лечат в основном
   NRE/логику метода. Но ошибки в Loki/Prometheus чаще **эксплуатационные**
   (конфигурация, место на диске, сеть, скрейп-таргеты) — их в коде нет, и агент
   честно упрётся в потолок. Стоит заранее решить: агент правит код или только
   готовит план/PR для человека.
4. **Недетерминизм диагноза**: детерминирован сбор фактов, но не вывод LLM;
   возможны колебания качества между ночами.
5. **Риск автономных правок**: даже с откатом и без push ночная правка кода
   требует дисциплины утреннего ревью веток `fix/ai-{runId}`.

---

## 4. Рекомендация

Продолжать, но **заморозить объём** и не наращивать функциональность:

1. Держать `CodeChange:Enabled=false`, пока агент не даст несколько ночей подряд
   качественных планов; первый целевой режим — «диагноз + план + отчёт», правка —
   опционально.
2. Для «лёгкости» собирать без Semantic Kernel (`EnableSemanticKernel=false`)
   и без Roslyn, пока анализ кода не понадобится всерьёз.
3. Если захочется сравнения с индустрией — собрать одноразовый прототип
   «cron-скрипт + headless Aider/Claude Code» и прогнать на тех же ночных данных;
   это даст честный бенчмарк качества
   [`NativeAgentOrchestrator`](../src/LLMNetHelper/LLMNetHelper.Agent/Orchestration/NativeAgentOrchestrator.cs)
   против лучших универсальных агентов — и, скорее всего, покажет, что связка
   безопасности и детерминизма у них отсутствует.

---

## Итог

Готовой альтернативы «под ключ» нет; проект — разумный путь, **все шаги цепочки
реализованы**, а главные риски — не технические, а в завышенных ожиданиях от того,
какие ошибки LLM реально способен исправить в коде.
