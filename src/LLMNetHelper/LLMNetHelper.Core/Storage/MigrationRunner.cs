using Microsoft.Data.Sqlite;

namespace LLMNetHelper.Core.Storage;

/// <summary>
/// Применение миграций к SQLite-хранилищу (план 22, этап 4.4).
/// <para>
/// Извлечён из <c>SqliteFactStore</c>: загрузка embedded SQL-миграций и их
/// применение — отдельный компонент, а не приватная деталь хранилища.
/// Включение WAL — часть инициализации БД и так же здесь.
/// </para>
/// </summary>
public sealed class MigrationRunner
{
    private const string InitMigrationResource = "LLMNetHelper.Core.Storage.Migrations.001_init.sql";

    private static int _providerInitialized;

    /// <summary>
    /// Инициализирует нативный провайдер SQLite (bundle_e_sqlite3) один раз на процесс.
    /// Без этого Microsoft.Data.Sqlite.Core бросает "You need to call SQLitePCL.raw.SetProvider()".
    /// </summary>
    private static void EnsureProvider()
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    /// <summary>
    /// Открывает подключение к хранилищу. Вызывает <see cref="EnsureProvider"/> один раз.
    /// </summary>
    public static string BuildConnectionString(string databasePath) =>
        $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared";

    /// <summary>
    /// Включает WAL и применяет все embedded миграции (сейчас — 001_init.sql).
    /// Идемпотентно для инкрементальных SQL; для больших рефакторингов миграций
    /// требуется версионирование (вне рамок).
    /// </summary>
    public async Task ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        EnsureProvider();

        var sql = LoadInitSql();

        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        using var migrateCmd = conn.CreateCommand();
        migrateCmd.CommandText = sql;
        await migrateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Загружает SQL инициализирующей миграции из embedded-ресурса.
    /// </summary>
    public static string LoadInitSql()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(InitMigrationResource);
        if (stream == null)
        {
            throw new InvalidOperationException(
                "Migration file 001_init.sql not found as embedded resource.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}