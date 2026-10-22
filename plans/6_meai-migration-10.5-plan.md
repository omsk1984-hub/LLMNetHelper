> **АРХИВНО.** Исторический документ происхождения проекта: пути, решения (src/Tools/ArtasTools.slnx) и каталоги в нём относятся к прежнему репозиторию Artas2026 и НЕ являются актуальной конфигурацией. Актуальное состояние: README.md и docs/.

# Миграция Microsoft.Extensions.AI: 9.0.0-preview.9 → 10.5.0

> Предворяющий шаг перед фазами 5–6 плана [`5_agent-sk-roslyn-rebuild-plan.md`](plans/5_agent-sk-roslyn-rebuild-plan.md:1).
> Цель: убрать фиксацию на preview-версии и привести `IChatClient` к актуальному контракту.

---

## 1. Причина миграции

Сейчас в проектах [`Llm`](src/LLMNetHelper/LLMNetHelper.Llm/LLMNetHelper.Llm.csproj:16) и
[`Agent`](src/LLMNetHelper/LLMNetHelper.Agent/LLMNetHelper.Agent.csproj:19) жёстко зафиксирована
версия **9.0.0-preview.9.24556.5**. Это создаёт три проблемы:

| Проблема | Последствие |
|---|---|
| Preview-версия | Нестабильный API, нет гарантий совместимости |
| Две версии в одном решении (10.5.0 приходила транзитивно с SK) | Риск `NU1605`, конфликт типов между сборками |
| Старый контракт `IChatClient` | `CompleteAsync`/`ChatCompletion` вместо `GetResponseAsync`/`ChatResponse` — код не совместим с документацией и с SK 1.81.0 |

Версии в кэше: `9.0.0-preview.9.24556.5`, `10.4.0`, **`10.5.0`**, `10.5.2`.
Целевая — **10.5.0** (та же, что требует `Microsoft.SemanticKernel.Connectors.[OI]` 1.81.0, §2.2 основного плана).

---

## 2. Что меняется в API

Проверено рефлексией по `Microsoft.Extensions.AI.Abstractions` 10.5.0:

| Было (preview.9) | Стало (10.5.0) |
|---|---|
| `Task<ChatCompletion> CompleteAsync(IList<ChatMessage>, ChatOptions?, CancellationToken)` | `Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage>, ChatOptions?, CancellationToken)` |
| `IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(...)` | `IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(...)` |
| `TService? GetService<TService>(object?)` | `object? GetService(Type, object?)` |
| Свойство `Metadata` (тип `ChatClientMetadata`) | **Отсутствует** в интерфейсе |
| `ChatCompletion.Message.Text` | `ChatResponse.Text` (агрегирующее свойство) |
| `ChatCompletion.FinishReason` | `ChatResponse.FinishReason` (тот же `ChatFinishReason?`) |
| `ChatCompletion.Usage` (`UsageDetails?`) | `ChatResponse.Usage` (тот же `UsageDetails?`) |
| — | `ChatResponse.Messages` (`IList<ChatMessage>`) |

`ChatMessage`, `ChatOptions`, `ChatRole`, `UsageDetails`, `ChatFinishReason` — без изменений.
`ChatOptions.Tools` / `ToolMode` / `AllowMultipleToolCalls` — доступны, tool calling поддерживается.

---

## 3. План работ

### Шаг 1 — Поднять версии пакетов

| Файл | Изменение |
|---|---|
| [`Llm.csproj`](src/LLMNetHelper/LLMNetHelper.Llm/LLMNetHelper.Llm.csproj:16) | `Microsoft.Extensions.AI`: preview.9 → **10.5.0**; `Microsoft.Extensions.AI.[OI]`: preview.9 → **10.5.0** |
| [`Agent.csproj`](src/LLMNetHelper/LLMNetHelper.Agent/LLMNetHelper.Agent.csproj:19) | `Microsoft.Extensions.AI.Abstractions`: preview.9 → **10.5.0** |
| [`Llm.csproj`](src/LLMNetHelper/LLMNetHelper.Llm/LLMNetHelper.Llm.csproj:18) | Проверить `System.Memory.Data`: при необходимости 8.0.1 → **10.0.3** (требование 10.5.0) |

**Проверка:** `dotnet restore` офлайн, отсутствие `NU1605`/`NU1608`.

### Шаг 2 — Переписать [`MeaiLlmClient`](src/LLMNetHelper/LLMNetHelper.Llm/MeaiLlmClient.cs:1)

Три изменения:

1. `CompleteAsync` → `GetResponseAsync`, `ChatCompletion` → `ChatResponse`.
2. `completion.Message?.Text` → `response.Text`.
3. `GetService<TService>(object?)` → `GetService(Type, object?)`; свойство `Metadata` удалить.
4. Убрать `using System.Runtime.CompilerServices;` (был для `[EnumeratorCancellation]`, не используется).

Контракт `IAnalysisLlmClient.CompleteAsync(LlmRequest, CancellationToken)` **сохраняется** —
это внутренний контракт проекта, он не зависит от версии MEAI.

### Шаг 3 — Переписать [`NativeAgentOrchestrator`](src/LLMNetHelper/LLMNetHelper.Agent/Orchestration/NativeAgentOrchestrator.cs:1)

1. `_chat.CompleteAsync(messages, cancellationToken: ct)` → `_chat.GetResponseAsync(messages, cancellationToken: ct)`.
2. `completion.Message?.Text` → `response.Text` — **в двух местах** (диагностика, планирование).
3. Тип `messages` — `List<ChatMessage>` совместим с `IEnumerable<ChatMessage>`, правок не требует.

### Шаг 4 — Обновить тестовый фейк

В [`NativeAgentOrchestratorTests`](src/LLMNetHelper/LLMNetHelper.Tests/Agent/NativeAgentOrchestratorTests.cs:18):

1. `ScriptedChatClient` реализует новый `IChatClient`:
   - `GetResponseAsync` вместо `CompleteAsync`;
   - возвращает `new ChatResponse(message) { FinishReason = ChatFinishReason.Stop, ModelId = "..." }`;
   - `GetService(Type, object?)` вместо generic-версии;
   - свойство `Metadata` удалить (интерфейс его больше не требует).
2. Записи `ReceivedMessages` — тип `IList<ChatMessage>` остаётся.

### Шаг 5 — Совместимость с SK 1.81.0 и статус `NU1605`

`Microsoft.SemanticKernel.Connectors.[OI]` 1.81.0 требует `Microsoft.Extensions.AI.[OI]` 10.5.0 —
после миграции версии **совпали**, риск `NU1605` со стороны MEAI устранён.

> **Результат проверки (факт).** Подавление `NU1605` убрать **не удалось**: после удаления
> полная пересборка (`--no-incremental`) упала с `NU1605` — но уже по **другой** причине:
>
> ```
> LLMNetHelper.Roslyn -> Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0
>   -> Microsoft.Build.Tasks.Core 17.14.28 -> System.CodeDom (>= 9.0.0)
> LLMNetHelper.Roslyn -> Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0
>   -> System.CodeDom (>= 7.0.0)        ← фактически резолвится 7.0.0
> ```
>
> Это конфликт внутри цепочки Roslyn/MSBuild, к MEAI отношения не имеющий.
> `System.CodeDom` нужен только на этапе загрузки решения (`MSBuildWorkspace`),
> в рантайм агента не попадает.
>
> **Решение:** подавление **возвращено** в [`Directory.Build.props`](src/LLMNetHelper/Directory.Build.props:16),
> но комментарий переписан: указана настоящая причина и отмечено, что прежняя (MEAI →
> `System.Memory.Data` 8.0.1) устранена миграцией.
>
> Альтернатива на будущее — добавить прямой `PackageReference` на `System.CodeDom` 9.0.0
> в проект `Roslyn`, но этого пакета нет в офлайн-кэше, поэтому выбран вариант с подавлением.

### Шаг 6 — Полный прогон

```
dotnet build src/LLMNetHelper/LLMNetHelper.Tests/LLMNetHelper.Tests.csproj
dotnet test  src/LLMNetHelper/LLMNetHelper.Tests/LLMNetHelper.Tests.csproj
```

Ожидание: 135/135 тестов зелёные, как до миграции.

---

## 4. Риски

| Риск | Вероятность | Митигация |
|---|---|---|
| `System.Memory.Data` 8.0.1 недостаточно для 10.5.0 | средняя | Уже в кэше 10.0.3 (пришла с SK); поднять версию |
| Изменилась семантика `ChatResponse.Text` относительно `Message.Text` | низкая | `Text` агрегирует все `Contents` с текстом; для одного сообщения результат идентичен |
| Тестовый фейк перестанет компилироваться из-за `Metadata` | высокая (ожидаемо) | Шаг 4 явно это покрывает |
| Потребители `MeaiLlmClient.Metadata` вне проекта | низкая | Поиск по решению перед удалением |
| Конфликт с `Microsoft.Extensions.AI` 9.x, приходящей транзитивно | низкая | После миграции 10.5.0 — старшая версия, конфликта нет |

---

## 5. Порядок выполнения

1. Поднять версии в `Llm.csproj` и `Agent.csproj`.
2. `dotnet restore` — проверить разрешение.
3. Переписать `MeaiLlmClient`.
4. Переписать `NativeAgentOrchestrator` (2 места).
5. Обновить `ScriptedChatClient` в тестах.
6. Сборка + тесты.
7. Проверить `NoWarn` в `Directory.Build.props`.

**Готово, когда:** 135/135 тестов зелёные, `NU1605` не требуется подавлять, версия MEAI — 10.5.0 во всех проектах.

---

## 6. Что НЕ входит в миграцию

- Фазы 5–6 основного плана (склейка в Worker, SK-оркестратор) — выполняются **после**.
- Переход `CustomOpenAiCompatibleClient` на tool calling — отдельная задача фазы 5.
- Замена транспорта: `IChatClient` в 10.5.0 берётся из `[OI]` 2.10.0 (в кэше), но подключение
  провайдера к `MeaiLlmClient` — часть фазы 5.
