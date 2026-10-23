# План оптимизации Semantic Kernel

Ниже представлен поэтапный план оптимизации приложений на базе **Semantic Kernel (SK)** с фокусом на снижение потребления токенов, уменьшение задержек и контроль затрат. План выстроен по приоритету: от «быстрых побед» до продвинутых архитектурных изменений.

---

## 📊 Фаза 0. Диагностика и телеметрия

Прежде чем оптимизировать — нужно измерить.

| Шаг | Действие | Инструменты SK |
|-----|----------|----------------|
| 0.1 | Включить OpenTelemetry-трейсинг | `services.AddSemanticKernelTelemetry()` |
| 0.2 | Логировать количество токенов (prompt/completion) | `OpenAIPromptExecutionSettings` + метрики |
| 0.3 | Измерить размер `ChatHistory` перед каждым вызовом | Кастомный `IChatCompletionService`-декоратор |
| 0.4 | Профилировать самые «тяжёлые» плагины | `IFunctionInvocationFilter` (post-execution) |

**Цель:** получить baseline — сколько токенов уходит на system prompt, history, tool outputs, и какие функции генерируют самый большой вывод.

---

## 🚀 Фаза 1. Quick Wins (1–2 дня)

### 1.1. Оптимизация `PromptExecutionSettings`
```csharp
var settings = new OpenAIPromptExecutionSettings
{
    MaxTokens = 2048,              // жёсткий лимит ответа
    Temperature = 0.2,             // меньше «воды» → меньше токенов
    TopP = 0.9,
    ChatSystemPrompt = "Отвечай кратко. Максимум 3 предложения.",
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
};
```

### 1.2. Ограничение размера `ChatHistory`
```csharp
public static class ChatHistoryExtensions
{
    public static void TrimToLastTurns(this ChatHistory history, int keepLast = 2)
    {
        // Оставляем system-message + последние N пар user/assistant
        var system = history.Where(m => m.Role == AuthorRole.System).ToList();
        var recent = history.Where(m => m.Role != AuthorRole.System)
                            .TakeLast(keepLast * 2).ToList();
        
        history.Clear();
        foreach (var m in system.Concat(recent)) history.Add(m);
    }
}
```

### 1.3. Короткие имена функций и описания
SK использует имена и описания функций как часть system prompt. Длинные XML-doc комментарии = лишние токены.
- Имена: `GetUser` вместо `GetUserProfileInformationFromDatabase`
- Описания: 1 предложение вместо абзаца

---

## 🔧 Фаза 2. Оптимизация вывода инструментов (главный источник потерь)

### 2.1. `IFunctionInvocationFilter` для пост-обработки результатов
Это **ключевая техника** для борьбы с «61% токенов на старые выводы».

```csharp
public class TruncatingFunctionFilter : IFunctionInvocationFilter
{
    private const int MaxOutputChars = 2000;

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, 
        Func<FunctionInvocationContext, Task> next)
    {
        await next(context);
        
        var result = context.Result.ToString();
        if (result.Length > MaxOutputChars)
        {
            // Обрезаем, оставляя начало и конец
            var head = result[..1000];
            var tail = result[^1000..];
            context.Result = new FunctionResult(
                context.Function, 
                $"{head}\n\n[... {result.Length - 2000} chars truncated ...]\n\n{tail}");
        }
    }
}

// Регистрация
kernel.FunctionInvocationFilters.Add(new TruncatingFunctionFilter());
```

### 2.2. Суммаризация старых tool-outputs через `IChatCompletionService`-декоратор
Перед отправкой запроса — проходим по истории и сжимаем старые tool-сообщения:

```csharp
public class SummarizingChatCompletion : IChatCompletionService
{
    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? settings = null, ...)
    {
        CompressOldToolOutputs(chatHistory, keepFullForLastTurns: 1);
        return await _inner.GetChatMessageContentsAsync(chatHistory, settings, ...);
    }
}
```

Логика сжатия:
- Ходы $T$ и $T-1$ → оставляем как есть
- Ходы $T-2$ и старше → заменяем на `[Tool {name} returned {size} chars: {1-line summary}]`

### 2.3. Возврат структурированных данных вместо текста
Плагины должны возвращать **JSON с нужными полями**, а не сырой вывод:
```csharp
// Плохо: return File.ReadAllText(path);
// Хорошо:
return new { 
    path, 
    lines = File.ReadLines(path).Take(50).ToArray(),
    totalLines = File.ReadLines(path).Count(),
    hasMore = true 
};
```

---

## 🧠 Фаза 3. Оптимизация промптов и шаблонов

### 3.1. Semantic Caching
Кэшировать ответы на идентичные промпты (особенно для RAG-запросов):
```csharp
// Можно использовать Redis / in-memory + хеш промпта
var cacheKey = ComputeHash(prompt + string.Join("|", relevantDocs));
if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
```

### 3.2. Prompt Templates с условной логикой
```handlebars
{{#if hasCodeContext}}
Контекст кода:
{{codeContext}}
{{/if}}

{{#if hasToolHistory}}
Предыдущие действия: {{summarizedHistory}}
{{/if}}

Задача: {{userQuery}}
```
Это позволяет динамически включать блоки только когда они нужны.

### 3.3. Разделение системного промпта
Вынести статическую часть (правила, формат ответа) в **Prompt Cache** (OpenAI/DeepSeek поддерживают `cache_control` для повторяющихся префиксов).

---

## 🗄️ Фаза 4. Оптимизация RAG и памяти

### 4.1. Vector Store: умный чанкинг
| Параметр | Рекомендация |
|----------|--------------|
| Chunk size | 400–600 токенов (не 1000+) |
| Overlap | 50–100 токенов |
| Top-K | 3–5 чанков (не 10–20) |
| Re-ranking | Использовать `Cohere Rerank` или cross-encoder после первичного поиска |

```csharp
var search = new VectorSearchQueryBuilder()
    .WithTopK(5)
    .WithMinRelevanceScore(0.75)  // отсечь шум
    .Build();
```

### 4.2. Иерархическая память
- **Краткосрочная:** `ChatHistory` (скользящее окно)
- **Долгосрочная:** Vector Store + суммаризация сессий
- **Эпизодическая:** ключевые факты в отдельном хранилище (например, JSON в `kernel.Data`)

---

## 🎯 Фаза 5. Model Routing и Agents

### 5.1. Маршрутизация по сложности задачи
```csharp
public class SmartRouter
{
    public string SelectModel(string prompt)
    {
        if (prompt.Length < 200 && !RequiresReasoning(prompt))
            return "deepseek-v3-lite";   // дёшево и быстро
        if (RequiresCodeGeneration(prompt))
            return "deepseek-coder";
        return "deepseek-v3";            // тяжёлая артиллерия
    }
}
```

### 5.2. SK Agents Framework — разделение ролей
Вместо одного «супер-агента» с огромным контекстом:
- **Planner Agent** — только планирует (маленькая модель)
- **Coder Agent** — пишет код (большая модель, но чистый контекст)
- **Reviewer Agent** — проверяет (изолированный контекст)

Каждый агент имеет свою `ChatHistory` → нет раздувания общего контекста.

### 5.3. Process Framework для оркестрации
Использовать `Microsoft.SemanticKernel.Processes` для построения графов задач, где каждый шаг — изолированная функция со своим контекстом.

---

## 📈 Фаза 6. Мониторинг и непрерывная оптимизация

### 6.1. KPI для отслеживания
| Метрика | Целевое значение |
|---------|------------------|
| Avg tokens/request | < 3000 |
| Tool output share | < 20% от контекста |
| Cache hit rate | > 30% |
| P95 latency | < 2s |
| Cost per task | Baseline × 0.4 |

### 6.2. A/B тестирование промптов
Использовать `PromptTemplateConfig` с версионированием и сравнивать метрики.

### 6.3. Feedback loop
Собирать случаи, где модель «забыла» контекст из-за агрессивного сжатия, и корректировать параметры `keepLast` / `MaxOutputChars`.

---

## 🗺️ Дорожная карта внедрения

```
Неделя 1: [Фаза 0] + [Фаза 1] → -30% токенов
Неделя 2: [Фаза 2] → -50% токенов на tool outputs
Неделя 3: [Фаза 3] + [Фаза 4] → улучшение качества RAG
Неделя 4: [Фаза 5] + [Фаза 6] → масштабирование и мониторинг
```

---

## ⚠️ Ключевые риски и как их митигировать

| Риск | Митигация |
|------|-----------|
| Модель «забывает» старый контекст после обрезки | Добавлять 1-строчное саммари вместо полного удаления |
| Обрезка JSON ломает парсинг | Обрезать только строковые поля, структуру сохранять |
| Кэш даёт устаревшие ответы | TTL + инвалидация по ключевым данным |
| Агенты теряют согласованность | Общий «blackboard» с фактами, доступный всем агентам |

---

Если хотите, могу детализировать любой из пунктов — например, написать готовый `IFunctionInvocationFilter` с суммаризацией через LLM, или показать интеграцию Semantic Caching с Redis.