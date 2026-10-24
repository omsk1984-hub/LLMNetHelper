namespace LLMNetHelper.Tests.TestInfrastructure;

/// <summary>
/// Общий читатель значений из <c>.env</c> для интеграционных тестов.
/// <para>
/// Логика перенесена из <c>GitLabIntegrationEnv</c> (план 19): сначала
/// переменная окружения процесса (её экспортирует <c>run_LLMNetHelper.ps1</c>),
/// иначе — первый найденный файл <c>.env</c>. Каталог <c>.env</c> определяется
/// подъёмом вверх от каталога сборки — без жёстко зашитого диска, чтобы тест
/// оставался переносимым.
/// </para>
/// <para>
/// Парсер примитивный — тот же, что в <c>run_LLMNetHelper.ps1</c>:
/// <c>KEY=VALUE</c>, разделение по первому <c>=</c>, кавычки обрезаются,
/// инлайн-комментарии не поддерживаются.
/// </para>
/// </summary>
internal static class EnvFileReader
{
    /// <summary>Явный путь к <c>.env</c> (переменная <c>{prefix}ENVFILE</c>).</summary>
    /// <param name="envFileVariable">Имя переменной с явным путём (например <c>GITLAB__ENVFILE</c>).</param>
    public static string? Read(string variable, string? envFileVariable = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(variable);

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        foreach (var candidate in EnumerateEnvFiles(envFileVariable))
        {
            var value = ReadValueFromFile(candidate, variable);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Кандидаты <c>.env</c>: явный путь (если задан), затем подъём от каталога
    /// сборки: <c>src/LLMNetHelper/.env</c> (корень репозитория агента) и
    /// <c>.env</c> каталога процесса.
    /// </summary>
    public static IEnumerable<string> EnumerateEnvFiles(string? envFileVariable = null)
    {
        var explicitPath = envFileVariable is null
            ? null
            : Environment.GetEnvironmentVariable(envFileVariable);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int level = 0; dir != null && level < 12; level++, dir = dir.Parent)
        {
            // Корень репозитория: src/LLMNetHelper/.env
            yield return Path.Combine(dir.FullName, "src", "LLMNetHelper", ".env");
            // Каталог самого проекта (вдруг тесты запускаются из него)
            yield return Path.Combine(dir.FullName, ".env");
        }
    }

    /// <summary>
    /// Читает значение ключа из файла <c>.env</c> (или <c>null</c>, если
    /// файл/ключ не найдены).
    /// </summary>
    public static string? ReadValueFromFile(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var idx = line.IndexOf('=');

            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx].Trim();

            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(idx + 1)..].Trim().Trim('"').Trim('\'');
        }

        return null;
    }
}
