using LLMNetHelper.Chat;
using LLMNetHelper.GitLab;
using LLMNetHelper.Tests.TestInfrastructure;
using LLMCore.Abstractions;
using LLMCore.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
// Алиас: LLMNetHelper.Chat.ChatOptions конфликтует с Microsoft.Extensions.AI.ChatOptions.
using ChatOpts = LLMNetHelper.Chat.ChatOptions;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Интеграционные тесты режима чата <c>--chat</c> (план 19).
/// <para>
/// Проверяют <b>реально</b>, сетью: сквозной прогон
/// фикстуры → BM25-поиск <see cref="ChatScope"/> → промпт →
/// <see cref="CustomOpenAiCompatibleClient"/>/<see cref="CustomLlmChatClient"/>
/// → ответ модели в <see cref="ChatSession"/>.
/// </para>
/// <para>
/// Креды берутся из <c>LLM__ENDPOINT</c>/<c>LLM__APIKEY</c> (окружение или
/// <c>src/LLMNetHelper/.env</c>, парсер — <see cref="EnvFileReader"/>).
/// Сетевые проверки выполняются <b>только</b> при <c>CHAT__INTEGRATION=1</c>;
/// иначе каждый тест имеет статус <c>Skipped</c> (а не Failed) — это сохраняет
/// требование «все тесты офлайн» (.roo/rules-code/01-test.md).
/// </para>
/// </summary>
public sealed class ChatIntegrationTests
{
    /// <summary>Уникальный маркер-«пароль» в фикстурах (см. <see cref="ChatIntegrationFixture.Marker"/>).</summary>
    private const string Marker = ChatIntegrationFixture.Marker;

    private const string Question =
        "Какой уникальный маркер упоминается в README сервиса demo-orders? " +
        "Приведи этот маркер дословно и укажи файл, где он найден.";

    /// <summary>Конфигурация LLM разрешается из окружения / .env.</summary>
    [ChatIntegrationFact]
    [Trait("Category", "Integration")]
    public void Config_LoadedFromEnvOrEnvironment()
    {
        var llm = ChatIntegrationEnv.RequireCredentials();

        Assert.StartsWith("http", llm.Endpoint, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(llm.ApiKey));
        Assert.False(string.IsNullOrWhiteSpace(llm.Model));
    }

    /// <summary>
    /// Режим A (-Ask, без /tools): реальный LLM отвечает на вопрос по фикстурам —
    /// ответ содержит маркер из README (проверка поиска ChatScope + транспорта
    /// + промпта одним сквозным прогоном).
    /// </summary>
    [ChatIntegrationFact]
    [Trait("Category", "Integration")]
    public void AskMode_RealLlm_ReturnsAnswerGroundedInFixtures()
    {
        using var fixture = new ChatIntegrationFixture();

        var (code, output) = fixture.RunSession(ask: Question);

        Assert.True(code == 0, $"Сеанс завершился кодом {code}. Вывод:{Environment.NewLine}{output}");
        Assert.DoesNotContain("[Сбой", output, StringComparison.Ordinal);
        Assert.Contains(Marker, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сужение области (/repo other-repo): маркер лежит только в demo-orders,
    /// поэтому ответ при области other-repo НЕ должен содержать маркер
    /// (допустимо «ничего не найдено» — утечка запрещена).
    /// </summary>
    [ChatIntegrationFact]
    [Trait("Category", "Integration")]
    public void AskMode_NarrowedRepo_DoesNotLeakOtherRepo()
    {
        using var fixture = new ChatIntegrationFixture();

        var (code, output) = fixture.RunSession(ask: Question, repo: "other-repo");

        Assert.True(code == 0, $"Сеанс завершился кодом {code}. Вывод:{Environment.NewLine}{output}");
        Assert.DoesNotContain(Marker, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Режим B (/tools): ReAct-цикл ChatAgentLoop с реальными инструментами
    /// repo_search/repo_read_file/repo_list_files и реальной моделью —
    /// итоговый ответ содержит маркер.
    /// </summary>
    [ChatIntegrationFact]
    [Trait("Category", "Integration")]
    public void ToolsMode_RealLlm_UsesRepoToolsAndAnswers()
    {
        using var fixture = new ChatIntegrationFixture();

        var (code, output) = fixture.RunSession(ask: Question, tools: true);

        Assert.True(code == 0, $"Сеанс завершился кодом {code}. Вывод:{Environment.NewLine}{output}");
        Assert.DoesNotContain("[Сбой", output, StringComparison.Ordinal);
        Assert.Contains(Marker, output, StringComparison.Ordinal);
    }
}

/// <summary>
/// Разрешение конфигурации интеграционных тестов чата: гейт по переменной
/// окружения, креды LLM (план 19, §2.1).
/// <para>
/// Порядок разрешения значения — тот же, что у реального запуска:
/// переменная окружения процесса → <c>src/LLMNetHelper/.env</c> →
/// <c>src/LLMNetHelper/LLMNetHelper/appsettings.json</c> (секция <c>Llm</c>).
/// Поэтому тест работает и когда эндпоинт задан только в appsettings.json
/// (не секрет), а ключ — в <c>.env</c> (секрет).
/// </para>
/// </summary>
internal static class ChatIntegrationEnv
{
    /// <summary>Переменная-гейт: только при <c>1</c>/<c>true</c> тесты выполняются.</summary>
    public const string GateVariable = "CHAT__INTEGRATION";

    public const string EndpointVariable = "LLM__ENDPOINT";
    public const string ApiKeyVariable = "LLM__APIKEY";
    public const string ModelVariable = "LLM__MODEL";
    public const string EnvFileVariable = "CHAT__ENVFILE";

    /// <summary>Дефолт модели — тот же, что в <c>LlmOptions.Model</c>.</summary>
    public const string DefaultModel = "qwen3.8-omni-flash";

    private static readonly Lazy<State> _state = new(Resolve);

    /// <summary>Причина пропуска теста либо <c>null</c>, если тест можно выполнять.</summary>
    public static string? SkipReason => _state.Value.SkipReason;

    /// <summary>Разрешённые креды (доступны только при отсутствии причины пропуска).</summary>
    public static LlmOptions RequireCredentials()
    {
        var state = _state.Value;

        if (state.SkipReason != null || state.Options == null)
        {
            throw new InvalidOperationException(
                $"Конфигурация LLM для чат-интеграции недоступна: {state.SkipReason ?? "неизвестная причина"}.");
        }

        return state.Options;
    }

    private static State Resolve()
    {
        if (!IsGateEnabled())
        {
            return new State(
                $"Интеграционные тесты чата выключены: установите ${GateVariable}='1' " +
                "(например: $env:CHAT__INTEGRATION='1'; dotnet test ... --filter Category=Integration).",
                null);
        }

        var appSettings = LoadAppSettingsLlm();

        var endpoint = ResolveSetting(EndpointVariable, appSettings, "Endpoint");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new State(
                $"Эндпоинт не найден: задайте {EndpointVariable} в окружении, в файле " +
                "src/LLMNetHelper/.env или в Llm:Endpoint appsettings.json.",
                null);
        }

        var apiKey = ResolveSetting(ApiKeyVariable, appSettings, "ApiKey");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new State(
                $"API-ключ не найден: задайте {ApiKeyVariable} в окружении или в файле " +
                "src/LLMNetHelper/.env.",
                null);
        }

        var model = ResolveSetting(ModelVariable, appSettings, "Model") ?? DefaultModel;

        var options = new LlmOptions
        {
            Provider = "custom",
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = model,
            // Интеграционный тест не должен долго ретраить: один повтор.
            MaxRetries = 1,
            TimeoutSeconds = 120
        };

        return new State(null, options);
    }

    /// <summary>
    /// Одно значение: окружение → .env (парсер <see cref="EnvFileReader"/>) →
    /// appsettings.json (секция Llm).
    /// </summary>
    private static string? ResolveSetting(string variable, IConfiguration? appSettings, string key)
    {
        var raw = EnvFileReader.Read(variable, EnvFileVariable);

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim();
        }

        var fromJson = appSettings?[$"Llm:{key}"];

        return string.IsNullOrWhiteSpace(fromJson) ? null : fromJson.Trim();
    }

    /// <summary>
    /// Читает <c>src/LLMNetHelper/LLMNetHelper/appsettings.json</c> подъёмом
    /// вверх от каталога сборки (тот же приём переносимости, что у .env).
    /// </summary>
    private static IConfiguration? LoadAppSettingsLlm()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int level = 0; dir != null && level < 12; level++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "LLMNetHelper", "LLMNetHelper", "appsettings.json");

            if (!File.Exists(candidate))
            {
                continue;
            }

            return new ConfigurationBuilder()
                .AddJsonFile(candidate, optional: false, reloadOnChange: false)
                .Build();
        }

        return null;
    }

    private static bool IsGateEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(GateVariable);

        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record State(string? SkipReason, LlmOptions? Options);
}

/// <summary>
/// Атрибут интеграционного факта чата: помечает тест как <c>Skipped</c>,
/// если гейт <c>CHAT__INTEGRATION</c> не включён или креды LLM не найдены.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ChatIntegrationFactAttribute : FactAttribute
{
    public ChatIntegrationFactAttribute()
    {
        var reason = ChatIntegrationEnv.SkipReason;

        if (reason != null)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// Фикстура чат-интеграции: временный CloneRoot с двумя «репозиториями»
/// и настоящий <see cref="IChatClient"/> через DI-регистрацию транспорта
/// <see cref="LlmClientRegistration.AddMonitorAgentLlm"/> (проверяется и она).
/// </summary>
internal sealed class ChatIntegrationFixture : IDisposable
{
    /// <summary>Уникальный маркер-«пароль» фикстуры; модель обязана процитировать его дословно.</summary>
    public const string Marker = "ЗебраМаркерИнтеграции777";

    private readonly ServiceProvider _provider;

    public ChatIntegrationFixture()
    {
        Llm = ChatIntegrationEnv.RequireCredentials();

        Root = Path.Combine(Path.GetTempPath(), $"chat_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);

        Write("demo-orders/README.md",
            $"# demo-orders\n\nСервис demo-orders принимает заказы по REST.\n\n" +
            $"Уникальный маркер этой интеграционной фикстуры: {Marker}\n");
        Write("demo-orders/src/OrderService.cs",
            "// demo-orders\n" +
            $"public static class OrderService {{ /* {Marker} */ public static void Accept() {{ }} }}\n");
        Write("other-repo/notes.md",
            "# other-repo\n\nПосторонний сервис про ленты и дельфины. Маркера здесь нет.\n");

        Scope = new ChatScope(new GitLabOptions { CloneRoot = Root }, new ChatOpts());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = Llm.Provider,
                ["Llm:Endpoint"] = Llm.Endpoint,
                ["Llm:ApiKey"] = Llm.ApiKey,
                ["Llm:Model"] = Llm.Model,
                ["Llm:MaxRetries"] = Llm.MaxRetries.ToString(),
                ["Llm:TimeoutSeconds"] = Llm.TimeoutSeconds.ToString()
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddMonitorAgentLlm(configuration);

        // TLS 1.2 для именованного клиента "llm": на этом хосте TLS 1.3 в SChannel
        // падает при рукопожатии (Win32Exception 0x80004005, см. docs/runbook.md —
        // то же лечение, что у GitLabFixture). Конфигурация складывается с
        // ConfigureHttpClient из AddMonitorAgentLlm (base address + Authorization).
        services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        });

        _provider = services.BuildServiceProvider();

        Chat = _provider.GetRequiredService<IChatClient>();
    }

    /// <summary>Абсолютный путь временного CloneRoot фикстуры.</summary>
    public string Root { get; }

    /// <summary>Область чата поверх фикстуры.</summary>
    public ChatScope Scope { get; }

    /// <summary>Настоящий LLM-транспорт (OpenAI-совместимый через MEAI-адаптер).</summary>
    public IChatClient Chat { get; }

    /// <summary>Разрешённые креды LLM.</summary>
    public LlmOptions Llm { get; }

    /// <summary>
    /// Сквозной прогонового сеанса чата без консоли: ввод пуст, вывод —
    /// в <see cref="StringWriter"/>.
    /// </summary>
    /// <returns>Код возврата сеанса и полный вывод.</returns>
    public (int Code, string Output) RunSession(string? ask = null, string? repo = null, bool tools = false)
    {
        var writer = new StringWriter();
        var session = new ChatSession(
            Chat,
            Scope,
            new ChatOpts(),
            Llm,
            NullLoggerFactory.Instance,
            new StringReader(""),
            writer);

        var code = session.RunAsync(ask, repo, tools, CancellationToken.None).GetAwaiter().GetResult();
        return (code, writer.ToString());
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, System.Text.Encoding.UTF8);
    }

    public void Dispose()
    {
        Chat.Dispose();
        _provider.Dispose();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
