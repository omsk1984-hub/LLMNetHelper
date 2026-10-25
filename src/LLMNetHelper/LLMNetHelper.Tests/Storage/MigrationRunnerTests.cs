using LLMNetHelper.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMNetHelper.Tests.Storage;

/// <summary>
/// Тесты компонента миграций и инициализации схемы (план 22, этап 4.4).
/// </summary>
public class MigrationRunnerTests
{
    private static string GetTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"llmnet-micro-{Guid.NewGuid():N}.db");

    [Fact]
    public void LoadInitSql_EmbeddedResource_ValidSql()
    {
        SqliteConnection.ClearAllPools();
        var sql = MigrationRunner.LoadInitSql();

        Assert.False(string.IsNullOrWhiteSpace(sql));
        // Миграция создаёт ключевые таблицы.
        Assert.Contains("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runs", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("log_facts", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error_samples", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_WalAndTables_Created()
    {
        SqliteConnection.ClearAllPools();
        var path = GetTempDbPath();
        try
        {
            var connectionString = MigrationRunner.BuildConnectionString(path);

            await new MigrationRunner().ApplyAsync(connectionString);

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='runs';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SchemaInitializer_IsIdempotent()
    {
        SqliteConnection.ClearAllPools();
        var path = GetTempDbPath();
        try
        {
            var initializer = new SqliteSchemaInitializer(path);

            await initializer.InitializeAsync();
            await initializer.InitializeAsync();

            await using var conn = new SqliteConnection(initializer.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('runs','log_facts');";
            Assert.Equal(2, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}