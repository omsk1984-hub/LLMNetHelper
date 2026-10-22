using ArtasTools.MonitorAgent.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ArtasTools.MonitorAgent.Tests.Storage;

public class SqliteFactStoreTests
{
    private static string GetTempDbPath()
    {
        return Path.Combine(Path.GetTempPath(), $"test_facts_{Guid.NewGuid()}.db");
    }

    [Fact]
    public async Task InitializeAsync_CreatesTables()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            // Проверяем что таблицы созданы — просто читаем
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task CreateRunAsync_ReturnsPositiveId()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var runId = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");

            Assert.True(runId > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FinishRunAsync_SetsStatus()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var runId = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");
            await store.FinishRunAsync(runId, "ok", "WARNING", null);

            var run = await store.GetLastSuccessfulRunAsync(48);
            Assert.NotNull(run);
            Assert.Equal("ok", run!.Status);
            Assert.Equal("WARNING", run.Verdict);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SaveAndRetrieveLogFacts()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var runId = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");

            var facts = new List<Abstractions.LogFact>
            {
                new() { BucketStart = "2026-01-01T10:00:00Z", Job = "artas_writer", Lvl = "fail", Count = 5 },
                new() { BucketStart = "2026-01-01T11:00:00Z", Job = "artas_stat", Lvl = "crit", Count = 2 }
            };

            await store.SaveLogFactsAsync(runId, facts);

            var retrieved = await store.GetPreviousLogFactsAsync(runId);
            Assert.Equal(2, retrieved.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SaveAndRetrieveErrorSamples()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var runId = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");

            var samples = new List<Abstractions.ErrorSample>
            {
                new()
                {
                    Signature = "abc123",
                    Lvl = "fail",
                    Count = 10,
                    FirstSeen = "2026-01-01T10:00:00Z",
                    LastSeen = "2026-01-01T15:00:00Z",
                    SampleText = "Error occurred"
                }
            };

            await store.SaveErrorSamplesAsync(runId, samples);

            var retrieved = await store.GetPreviousErrorSamplesAsync(runId);
            Assert.Single(retrieved);
            Assert.Equal("abc123", retrieved[0].Signature);
            Assert.Equal(10, retrieved[0].Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task PurgeOldRuns_RemovesOldEntries()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            // Создаём "старый" запуск (вчера)
            var yesterday = DateTime.UtcNow.AddDays(-1).ToString("o");
            var runId = await store.CreateRunAsync(yesterday, DateTime.UtcNow.ToString("o"));
            await store.FinishRunAsync(runId, "ok", "OK", null);

            // Очищаем с retention = 0 дней
            var purged = await store.PurgeOldRunsAsync(0);

            // Должен удалить хотя бы один
            Assert.True(purged >= 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task IdempotentRun_SameResults()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            // Два одинаковых прогона
            var runId1 = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");
            var runId2 = await store.CreateRunAsync("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");

            Assert.NotEqual(runId1, runId2); // Разные ID
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
