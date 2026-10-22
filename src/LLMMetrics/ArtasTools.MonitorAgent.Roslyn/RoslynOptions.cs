namespace ArtasTools.MonitorAgent.Roslyn;

/// <summary>
/// Параметры слоя анализа кода.
/// </summary>
public sealed class RoslynOptions
{
    /// <summary>Секция конфигурации.</summary>
    public const string SectionName = "Roslyn";

    /// <summary>
    /// Включён ли анализ кода. Отключается в окружениях без полного MSBuild SDK
    /// (например, в runtime-образе контейнера).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Путь к решению (.sln / .slnx) или проекту (.csproj).</summary>
    public string SolutionPath { get; set; } = "";

    /// <summary>Сколько секунд держать загруженную компиляцию в кэше.</summary>
    public int CompilationCacheSeconds { get; set; } = 300;

    /// <summary>Максимальная длина исходника члена при чтении, в символах.</summary>
    public int MaxSourceChars { get; set; } = 20000;

    /// <summary>Максимальное число результатов поиска символов.</summary>
    public int MaxSearchResults { get; set; } = 50;
}
