using System.Net.Http.Headers;
using LLMCore.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LLMCore.Llm;

/// <summary>
/// Регистрация LLM-клиентов в DI.
/// <para>
/// Транспорт один — собственный HttpClient с полным контролем над JSON-схемой
/// (<see cref="CustomOpenAiCompatibleClient"/>). Провайдер <c>meai</c>
/// исторически означал отдельный транспорт, но в текущей реализации
/// является алиасом <c>custom</c>: конфигурация принимается без изменений
/// поведения.
/// </para>
/// <para>
/// Для агентского оркестратора дополнительно регистрируется
/// <see cref="IChatClient"/> — тонкая обёртка над тем же транспортом
/// (см. <see cref="CustomLlmChatClient"/>), поэтому оркестратор
/// работает без второго HTTP-клиента.
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

        var provider = (llmOptions.Provider ?? "custom").Trim().ToLowerInvariant();

        // meai — алиас custom: единый транспорт, единая регистрация.
        if (provider != "custom" && provider != "meai")
        {
            throw new InvalidOperationException(
                $"Неизвестный LLM-провайдер: '{llmOptions.Provider}'. Допустимо: custom, meai.");
        }

        RegisterNamedHttpClient(services, llmOptions);

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
