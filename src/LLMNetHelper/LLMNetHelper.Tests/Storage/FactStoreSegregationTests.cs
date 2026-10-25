using System.Reflection;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Storage;
using Xunit;

namespace LLMNetHelper.Tests.Storage;

/// <summary>
/// Тесты сегрегации хранилища фактов (план 22, этап 2.7):
/// жирный <see cref="IFactStore"/> разложен на узкие репозитории
/// (<see cref="IRunRepository"/>, <see cref="ILogFactRepository"/>,
/// <see cref="IErrorSampleRepository"/>, <see cref="IMetricFactRepository"/>,
/// <see cref="ILlmRunRepository"/>); из контракта убраны <c>IDisposable</c>
/// (LSP: не каждая реализация обязана утилизироваться) и
/// <c>InitializeAsync</c> (временно́е зацепление — инициализация становится
/// внутренней деталью реализации).
/// </summary>
public class FactStoreSegregationTests
{
    private static string GetTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"test_seg_{Guid.NewGuid()}.db");

    /// <summary>
    /// IFactStore — фасад: наследует все пять узких репозиториев.
    /// </summary>
    [Fact]
    public void FactStore_IsFacadeOfNarrowRepositories()
    {
        Assert.True(typeof(IRunRepository).IsAssignableFrom(typeof(IFactStore)));
        Assert.True(typeof(ILogFactRepository).IsAssignableFrom(typeof(IFactStore)));
        Assert.True(typeof(IErrorSampleRepository).IsAssignableFrom(typeof(IFactStore)));
        Assert.True(typeof(IMetricFactRepository).IsAssignableFrom(typeof(IFactStore)));
        Assert.True(typeof(ILlmRunRepository).IsAssignableFrom(typeof(IFactStore)));
    }

    /// <summary>
    /// Из контракта убран IDisposable: реализации не обязаны утилизироваться
    /// (LSP, план 22).
    /// </summary>
    [Fact]
    public void FactStore_DoesNotRequireDisposable()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(IFactStore)));
    }

    /// <summary>
    /// Из контракта убран InitializeAsync: инициализация — внутренняя деталь
    /// реализации (временно́е зацепление, план 22).
    /// </summary>
    [Fact]
    public void FactStore_DoesNotDeclareInitialize()
    {
        var method = typeof(IFactStore).GetMethod(
            "InitializeAsync", BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(method);
    }

    /// <summary>
    /// Реализация обязана работать БЕЗ ручного InitializeAsync:
    /// миграции применяются лениво при первой операции.
    /// На текущем коде падает с "no such table: runs" (красный).
    /// </summary>
    [Fact]
    public async Task SqliteFactStore_OperationsAutoInitialize()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);

            // Без вызова InitializeAsync — операция обязана сама инициализировать БД.
            var runId = await store.CreateRunAsync(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            Assert.True(runId > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// SqliteFactStore реализует все узкие репозитории (единственная реализация).
    /// </summary>
    [Fact]
    public void SqliteFactStore_ImplementsAllNarrowRepositories()
    {
        var type = typeof(SqliteFactStore);

        Assert.True(typeof(IRunRepository).IsAssignableFrom(type));
        Assert.True(typeof(ILogFactRepository).IsAssignableFrom(type));
        Assert.True(typeof(IErrorSampleRepository).IsAssignableFrom(type));
        Assert.True(typeof(IMetricFactRepository).IsAssignableFrom(type));
        Assert.True(typeof(ILlmRunRepository).IsAssignableFrom(type));
    }
}
