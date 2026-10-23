using System.Net.Http.Headers;
using LLMNetHelper.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LLMCore.Llm;

/// <summary>
/// Регистрация LLM-клиентов в DI.
/// <para>
/// Выбор реализации зависит от <see cref="LlmOptions.Provider"/>:
/// <list type="bullet">
///   <item><c>custom</c> — свой HttpClient с полным контролем над JSON-схемой;</item>
///   <item><c>meai</c> — Microsoft.Extensions.AI 10.5.0 через SDK-провайдер.</item>
/// </list>
/// </para>
/// <para>
/// Для агентского оркестратора дополнительно регистрируется
/// <see cref="IChatClient"/>. При <c>custom</c> это тонкая обёртка над тем же
/// транспортом (см. <see cref="CustomLlmChatClient"/>).
/// </para>
/// </summary>
public static class LlmClientRegistration
{
    /// <summary>Имя именованного HttpClient для LLM-транспорта.</summary>
    private const string LlmHttpClientName = "llm";

    /// <summary>
    /// Регистрирует <see cref="IAnalysisLlmClient"/> и <see cref="IChatClient"/>
    /// в зависимости от конфигурации.
    /// </summary>
    public static IServiceCollection AddMonitorAgentLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection("Llm"));

        var llmOptions = configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();

        RegisterNamedHttpClient(services, llmOptions);

        var provider = (llmOptions.Provider ?? "custom").Trim().ToLowerInvariant();

        switch (provider)
        {
            case "custom":
                services.AddSingleton<IAnalysisLlmClient>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    return new CustomOpenAiCompatibleClient(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        options);
                });

                // IChatClient поверх того же транспорта — чтобы оркестратор
                // работал и на custom-провайдере.
                services.AddSingleton<IChatClient>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    return new CustomLlmChatClient(
                        sp.GetRequiredService<IAnalysisLlmClient>(),
                        options);
                });
                break;

            case "meai":
                // Транспорт MEAI строится поверх того же [OI]-совместимого
                // HTTP-клиента, что и custom: MeaiLlmClient реализует IChatClient,
                // а низкоуровневый SDK-транспорт подключается отдельно
                // (см. README, раздел «Провайдер meai»).
                services.AddSingleton<IAnalysisLlmClient>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    return new CustomOpenAiCompatibleClient(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        options);
                });

                services.AddSingleton<IChatClient>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<LlmOptions>>();
                    return new CustomLlmChatClient(
                        sp.GetRequiredService<IAnalysisLlmClient>(),
                        options);
                });
                break;

            default:
                throw new InvalidOperationException(
                    $"Неизвестный LLM-провайдер: '{llmOptions.Provider}'. Допустимо: custom, meai.");
        }

        return services;
    }

    /// <summary>
    /// Регистрирует именованный HttpClient с base address и авторизацией.
    /// </summary>
    private static void RegisterNamedHttpClient(IServiceCollection services, LlmOptions llmOptions)
    {
        services.AddHttpClient(LlmHttpClientName).ConfigureHttpClient(client =>
        {
            if (Uri.TryCreate(llmOptions.Endpoint, UriKind.Absolute, out var endpoint))
            {
                client.BaseAddress = endpoint;
            }

            if (!string.IsNullOrWhiteSpace(llmOptions.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", llmOptions.ApiKey);
            }
        });
    }
}
