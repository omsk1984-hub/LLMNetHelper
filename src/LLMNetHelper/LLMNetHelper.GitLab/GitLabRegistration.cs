using LLMNetHelper.Abstractions;
using LLMNetHelper.GitLab;
using Microsoft.Extensions.DependencyInjection;

namespace LLMNetHelper.GitLab;

/// <summary>
/// Регистрация GitLab-функциональности в DI.
/// </summary>
public static class GitLabRegistration
{
    /// <summary>
    /// Регистрирует GitLab-сервисы и инструменты агента.
    /// </summary>
    public static IServiceCollection AddGitLab(this IServiceCollection services)
    {
        // HTTP-клиент для GitLab API создаётся ЯВНО (а не через IHttpClientFactory):
        // ограничение протоколов TLS должно гарантированно попасть в первичный
        // HttpClientHandler. На хостах с Windows-прокси/SChannel рукопожатие
        // TLS 1.3 падает с Win32Exception 0x80004005 («Не удается установить связь
        // с локальной системой безопасности») — GitLab:TlsProtocols="Tls12" лечит это.
        // План 22, этап 2.3: регистрация по интерфейсу IGitLabApiClient.
        services.AddSingleton<IGitLabApiClient>(sp =>
        {
            var options = sp.GetRequiredService<GitLabOptions>();

            var handler = new HttpClientHandler();

            var protocols = ParseTlsProtocols(options.TlsProtocols);

            if (protocols.HasValue)
            {
                handler.SslProtocols = protocols.Value;
            }

            var http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            return new GitLabApiClient(options, http);
        });

        // Сервисы
        services.AddSingleton<GitLabService>();

        // Инструменты агента
        services.AddSingleton<IAgentTool, GitLabListReposTool>();
        services.AddSingleton<IAgentTool, GitLabCloneAllTool>();
        services.AddSingleton<IAgentTool, GitLabUpdateRepoTool>();

        return services;
    }

    /// <summary>
    /// Разбирает список TLS-протоколов из конфигурации ("Tls12", "Tls12, Tls13").
    /// </summary>
    /// <returns>Флаг протоколов или <c>null</c>, если строка пуста/нераспознана.</returns>
    internal static System.Security.Authentication.SslProtocols? ParseTlsProtocols(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return null;
        }

        // Накопление ведётся в НЕ nullable локаль: поднятый оператор `|` для
        // Nullable<T> возвращает null, если хоть один операнд null, поэтому
        // запись вида `SslProtocols? r = null; r |= parsed;` навсегда
        // оставляла бы r равным null и ограничение протоколов не применялось.
        var accumulated = System.Security.Authentication.SslProtocols.None;

        foreach (var part in config.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<System.Security.Authentication.SslProtocols>(part, ignoreCase: true, out var parsed))
            {
                return null;
            }

            accumulated |= parsed;
        }

        return accumulated == System.Security.Authentication.SslProtocols.None ? null : accumulated;
    }
}
