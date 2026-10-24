# План: интеграционный тест режима `-Chat` (реальный LLM по сети)

Статус: черновик на согласование. Дата: 2026-10-24 (UTC).
Решение пользователя: **вариант 1** — реальный вызов LLM по сети, гейт
`CHAT__INTEGRATION=1`, без кредов тесты имеют статус `Skipped` (паттерн
[`GitLabIntegrationTests`](../src/LLMNetHelper/LLMNetHelper.Tests/GitLab/GitLabIntegrationTests.cs)).

## 1. Цель

Проверить режим чата **реальным** сквозным прогоном: фикстуры файлов во
временном `CloneRoot` → BM25-поиск [`ChatScope`](../src/LLMNetHelper/LLMNetHelper/Chat/ChatScope.cs)
→ промпт [`ChatPromptBuilder`](../src/LLMNetHelper/LLMNetHelper/Chat/ChatPromptBuilder.cs)
→ реальный транспорт [`CustomOpenAiCompatibleClient`](../src/LLMCore/LLMCore.Llm/CustomOpenAiCompatibleClient.cs)
через [`CustomLlmChatClient`](../src/LLMCore/LLMCore.Llm/CustomLlmChatClient.cs)
→ ответ модели в [`ChatSession`](../src/LLMNetHelper/LLMNetHelper/Chat/ChatSession.cs).

Офлайн-прогон решения остаётся зелёным: без гейта все тесты `Skipped`.

## 2. Новый файл

`src/LLMNetHelper/LLMNetHelper.Tests/Chat/ChatIntegrationTests.cs`:

### 2.1. `ChatIntegrationEnv` (по образцу `GitLabIntegrationEnv`)

- Гейт: `CHAT__INTEGRATION` (`1`/`true`).
- Креды: `LLM__ENDPOINT`, `LLM__APIKEY`, опционально `LLM__MODEL`
  (дефолт `qwen3.8-omni-flash` — см. §6) — из окружения, иначе из
  `src/LLMNetHelper/.env`
  (тот же подъём каталогов от `AppContext.BaseDirectory` и тот же парсер
  `KEY=VALUE`; код парсера переиспользуется копированием, как в GitLab-тесте,
  либо выносится в общий `internal static class EnvFileReader` — выбрать второе,
  чтобы не дублировать ~40 строк).
- `SkipReason`: гейт выключен / пуст `LLM__ENDPOINT` / пуст `LLM__APIKEY` —
  с подсказкой, что задать.
- `RequireLlmOptions()` → `LlmOptions` (Endpoint, ApiKey, Model,
  TimeoutSeconds=120, MaxRetries=1 — интеграционный тест не должен долго
  ретраить).

### 2.2. `ChatIntegrationFactAttribute`

`FactAttribute` с `Skip = ChatIntegrationEnv.SkipReason` — как
`GitLabIntegrationFactAttribute`. Каждый тест дополнительно помечается
`[Trait("Category", "Integration")]`.

### 2.3. Фикстура `ChatIntegrationFixture : IDisposable`

- Временный каталог `chat_int_{guid}` с двумя «репозиториями»:
  - `demo-orders/README.md` — текст с уникальным маркером
    (например `МаркерИнтеграцииЗебра-777`: «Сервис demo-orders принимает
    заказы. Пароль: МаркерИнтеграцииЗебра-777»);
  - `demo-orders/src/OrderService.cs` — псевдокод с тем же маркером;
  - `other-repo/notes.md` — посторонний текст (проверка, что поиск не
    «вездесущий», а по запросу).
- `GitLabOptions { CloneRoot = <временный каталог> }`, `ChatOptions` с
  дефолтами; `ChatScope`.
- Реальный `IChatClient` через DI-регистрацию транспорта (проверяется и сам
  [`LlmClientRegistration.AddMonitorAgentLlm`](../src/LLMCore/LLMCore.Llm/LlmClientRegistration.cs:35)):
  `ServiceCollection` + `AddHttpClient()` + `AddMonitorAgentLlm` с
  `ConfigurationBuilder.AddInMemoryCollection` (`Llm:Endpoint`, `Llm:ApiKey`,
  `Llm:Model`, `Llm:MaxRetries=1`) → `GetRequiredService<IChatClient>()`.
- `ChatSession` с `TextReader`/`TextWriter` на `StringReader`/`StringWriter`
  (консоль не нужна), `NullLoggerFactory`.
- `Dispose()` — удаление временного каталога.

### 2.4. Тесты (каждый `[ChatIntegrationFact]` + трейт)

1. `Config_LoadedFromEnvOrEnvironment` — креды разрешены: `Endpoint`
   начинается с `http`, `ApiKey` непустой, `Model` непустая.
2. `AskMode_RealLlm_ReturnsAnswerGroundedInFixtures` —
   `session.RunAsync(ask: "Какой маркер упоминается в документации demo-orders?",
   repo: null, tools: false)` → код 0; ответ непустой, не начинается с
   `[Сбой`; содержит маркер `МаркерИнтеграцииЗебра-777` (модель обязана
   процитировать фрагмент из фикстуры — это и есть проверка поиска +
   транспорта + промпта).
3. `AskMode_NarrowedRepo_DoesNotLeakOtherRepo` — вопрос про маркер при
   `repo: "other-repo"` → ответ НЕ содержит маркер (проверка сужения области
   `/repo` на реальном стеке). Ассерты мягкие: допускается, что модель
   ответит «не найдено»; запрещён только сам маркер в ответе.
4. `ToolsMode_RealLlm_UsesRepoSearchAndAnswers` — `tools: true`: ReAct-цикл
   [`ChatAgentLoop`](../src/LLMNetHelper/LLMNetHelper/Chat/ChatAgentLoop.cs)
   с реальными инструментами `repo_search`/`repo_read_file`/`repo_list_files`;
   вопрос про маркер → ответ содержит маркер. Тест допускает до
   `ChatOptions.MaxSteps` итераций; если модель отвечает сразу без
   инструментов — это тоже успех (ассерт только по наличию маркера).

Таймаут на тест: `[Fact(Timeout = 180000)]` не используется (xUnit-факт без
таймаута); защита — `LlmOptions.TimeoutSeconds=120` + `MaxRetries=1`.

## 3. Сопутствующие правки

1. Вынести парсер `.env` из [`GitLabIntegrationTests.cs`](../src/LLMNetHelper/LLMNetHelper.Tests/GitLab/GitLabIntegrationTests.cs)
   в общий `internal static class EnvFileReader`
   (`LLMNetHelper.Tests/TestInfrastructure/EnvFileReader.cs`): `EnumerateEnvFiles`,
   `ReadValueFromFile`. GitLab-тест переключить на него (поведение без изменений).
2. [`src/LLMNetHelper/.env.example`](../src/LLMNetHelper/.env.example):
   комментарий-блок про `CHAT__INTEGRATION=1` (аналог блока `GITLAB__INTEGRATION`).
3. [`docs/runbook.md`](../docs/runbook.md) §«Интеграционные тесты (сеть)»:
   добавить абзац про чат-интеграцию (`CHAT__INTEGRATION=1` + LLM-креды,
   что проверяется).
4. [`docs/configuration.md`](../docs/configuration.md) §«Чат»: строка про
   интеграционный тест и его гейт.

## 4. Ограничения и риски

| Риск | Мера |
|---|---|
| Модель может не вернуть маркер дословно (перефразирует) | Маркер — «слово-пароль» без пробелов; промпт просит процитировать; при нестабильности ассерт ослабляется до «ответ не сбой + упомянут файл» |
| Тест тратит токены реального провайдера | Гейт по умолчанию выключен; один короткий вопрос на тест |
| Пустые креды в `.env` | Тесты `Skipped` с понятной причиной — как у GitLab |
| Сетевой сбой провайдера | `MaxRetries=1`; сбой = падение интеграционного теста (допустимо, это и есть его смысл) |
| Временные файлы | `IDisposable`, удаление в `finally` |

## 5. Шаги реализации (для Code mode)

1. `TestInfrastructure/EnvFileReader.cs` + рефакторинг `GitLabIntegrationEnv` на него.
2. `Chat/ChatIntegrationTests.cs` (env-гейт, атрибут, фикстура, 4 теста).
3. `.env.example`, `docs/runbook.md`, `docs/configuration.md`.
4. `dotnet test src/LLMNetHelper/LLMNetHelper.slnx` — офлайн-прогон: новые
   тесты должны быть `Skipped` (гейт выключен), остальные зелёные.
5. Прогон с гейтом (при наличии кредов):
   `$env:CHAT__INTEGRATION='1'; dotnet test src/LLMNetHelper/LLMNetHelper.slnx --filter Category=Integration`.

## 6. Замена дефолта модели: `gpt-4.1-mini` → `qwen3.8-omni-flash`

Дополнительное требование пользователя. Меняется во всех источниках
(артефакты `bin/` не трогаем — перегенерируются сборкой):

| Файл | Что менять |
|---|---|
| [`src/LLMCore/LLMCore.Abstractions/LlmOptions.cs`](../src/LLMCore/LLMCore.Abstractions/LlmOptions.cs) (стр. 20–21) | дефолт `Model` + XML-док |
| [`src/LLMNetHelper/LLMNetHelper/appsettings.json`](../src/LLMNetHelper/LLMNetHelper/appsettings.json) | `"Model": "qwen3.8-omni-flash"` |
| [`src/LLMNetHelper/LLMNetHelper/appsettings.example.json`](../src/LLMNetHelper/LLMNetHelper/appsettings.example.json) (стр. 72) | то же + пример в комментарии |
| [`src/LLMNetHelper/run_LLMNetHelper.ps1`](../src/LLMNetHelper/run_LLMNetHelper.ps1) (стр. 198) | `Get-EnvOr "LLM__MODEL" "qwen3.8-omni-flash"` |
| [`deploy/monitor-agent/monitor-agent.env.example`](../deploy/monitor-agent/monitor-agent.env.example) (стр. 23) | `LLM__MODEL=qwen3.8-omni-flash` |
| [`docs/configuration.md`](../docs/configuration.md) (стр. 82) | дефолт в таблице `Llm:*` |
| [`src/LLMNetHelper/LLMNetHelper.Tests/UtcConsistencyTests.cs`](../src/LLMNetHelper/LLMNetHelper.Tests/UtcConsistencyTests.cs) (стр. 91) | ассерт дефолта `LlmOptions` → новая модель |
| [`src/LLMNetHelper/LLMNetHelper.Tests/Llm/LlmContractParityTests.cs`](../src/LLMNetHelper/LLMNetHelper.Tests/Llm/LlmContractParityTests.cs) (стр. 20, 29) | литералы в тесте рекорда → новая модель (консистентность) |

Проверка: `dotnet test src/LLMNetHelper/LLMNetHelper.slnx` зелёный;
`run_LLMNetHelper.ps1 -Info` печатает `Model: qwen3.8-omni-flash`.
