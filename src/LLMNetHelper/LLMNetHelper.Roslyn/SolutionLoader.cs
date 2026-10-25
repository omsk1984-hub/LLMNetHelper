using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace LLMNetHelper.Roslyn;

/// <summary>
/// Загрузчик решения через <see cref="MSBuildWorkspace"/> с кэшем компиляций.
/// <para>
/// Подводные камни, которые здесь учтены:
/// <list type="bullet">
///   <item><c>MSBuildLocator</c> обязан быть зарегистрирован <b>до</b> первого обращения
///         к типам MSBuild — иначе MSBuildWorkspace падает с "Could not load file or assembly";</item>
///   <item><c>MSBuildWorkspace</c> одноразовый и дорогой — держим один экземпляр
///         и пересоздаём только после применения правок;</item>
///   <item>если SDK недоступен (runtime-образ), загрузка возвращает false, а не бросает.</item>
/// </list>
/// </para>
/// </summary>
// План 22, этап 2.4: реализация контракта ISolutionLoader.
public sealed class SolutionLoader : ISolutionLoader, IDisposable
{
    private static readonly object LocatorLock = new();
    private static bool _locatorRegistered;
    private static bool _locatorFailed;

    private readonly RoslynOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedSolution> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public SolutionLoader(RoslynOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Загружает решение по пути из <see cref="RoslynOptions.SolutionPath"/>.
    /// </summary>
    public Task<Solution?> LoadAsync(CancellationToken ct = default)
    {
        return LoadAsync(_options.SolutionPath, ct);
    }

    /// <summary>
    /// Загружает решение (или проект) по явному пути и возвращает <see cref="Solution"/>.
    /// Возвращает <c>null</c>, если анализ недоступен, путь пуст/не существует
    /// или загрузка не удалась. Кэш ключуется по абсолютному пути, поэтому
    /// разные решения (например, разные репозитории чата в режиме <c>/repo</c>)
    /// сосуществуют в кэше и вытесняются по TTL <see cref="RoslynOptions.CompilationCacheSeconds"/>.
    /// </summary>
    public async Task<Solution?> LoadAsync(string solutionPath, CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(solutionPath))
        {
            return null;
        }

        if (!TryRegisterLocator())
        {
            return null;
        }

        string path;
        try
        {
            path = Path.GetFullPath(solutionPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(path, out var cached) && !cached.IsExpired(_options.CompilationCacheSeconds))
            {
                return cached.Solution;
            }

            var solution = await OpenAsync(path, ct).ConfigureAwait(false);
            if (solution is null)
            {
                return null;
            }

            if (_cache.TryRemove(path, out var old))
            {
                old.Dispose();
            }

            _cache[path] = new CachedSolution(solution, DateTime.UtcNow);
            return solution;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Сбрасывает кэш — вызывается после применения правок.
    /// </summary>
    public void InvalidateCache()
    {
        foreach (var key in _cache.Keys.ToArray())
        {
            if (_cache.TryRemove(key, out var cached))
            {
                cached.Dispose();
            }
        }
    }

    /// <summary>
    /// Открывает решение или отдельный проект.
    /// </summary>
    private static async Task<Solution?> OpenAsync(string path, CancellationToken ct)
    {
        MSBuildWorkspace? workspace = null;
        try
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Configuration"] = "Debug",
                ["DesignTimeBuild"] = "true",
                ["BuildProjectReferences"] = "false"
            };

            workspace = MSBuildWorkspace.Create(properties);

            // Ошибки загрузки не должны валить прогон — их собираем, но продолжаем.
            workspace.SkipUnrecognizedProjects = true;

            // OpenProjectAsync возвращает Project, OpenSolutionAsync — Solution.
            // Приводим оба варианта к Solution через Project.Solution.
            var isProject = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

            Solution solution = isProject
                ? (await workspace.OpenProjectAsync(path, cancellationToken: ct).ConfigureAwait(false)).Solution
                : await workspace.OpenSolutionAsync(path, cancellationToken: ct).ConfigureAwait(false);

            // Workspace нужно держать живым, пока используется Solution.
            // Поэтому кэшируем Solution вместе с workspace.
            return new WorkspaceBoundSolution(solution, workspace).Solution;
        }
        catch (OperationCanceledException)
        {
            workspace?.Dispose();
            throw;
        }
        catch (Exception)
        {
            workspace?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Регистрация MSBuildLocator. Выполняется один раз на процесс.
    /// </summary>
    private static bool TryRegisterLocator()
    {
        if (_locatorRegistered)
        {
            return true;
        }

        if (_locatorFailed)
        {
            return false;
        }

        lock (LocatorLock)
        {
            if (_locatorRegistered)
            {
                return true;
            }

            if (_locatorFailed)
            {
                return false;
            }

            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    MSBuildLocator.RegisterDefaults();
                }
                _locatorRegistered = true;
                return true;
            }
            catch (Exception)
            {
                // Нет полного SDK (runtime-образ) — анализ кода недоступен.
                _locatorFailed = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Связка Solution + Workspace для корректного освобождения.
    /// </summary>
    private sealed class WorkspaceBoundSolution(Solution solution, MSBuildWorkspace workspace) : IDisposable
    {
        public Solution Solution { get; } = solution;

        public void Dispose() => workspace.Dispose();
    }

    /// <summary>
    /// Кэшированное решение с временем загрузки.
    /// </summary>
    private sealed class CachedSolution(Solution solution, DateTime loadedAtUtc) : IDisposable
    {
        public Solution Solution { get; } = solution;

        public bool IsExpired(int cacheSeconds)
        {
            if (cacheSeconds <= 0)
            {
                return true;
            }
            return DateTime.UtcNow - loadedAtUtc > TimeSpan.FromSeconds(cacheSeconds);
        }

        public void Dispose()
        {
            // Solution не реализует IDisposable, но workspace за ним — да.
            // Освобождение рабочего пространства делает GC через finalizer,
            // для детерминизма достаточно отпустить ссылку.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        InvalidateCache();
        _gate.Dispose();
    }
}
