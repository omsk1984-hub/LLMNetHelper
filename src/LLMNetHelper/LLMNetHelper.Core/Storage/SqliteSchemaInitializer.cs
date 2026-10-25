namespace LLMNetHelper.Core.Storage;

/// <summary>
/// Ленивая идемпотентная инициализация SQLite-схемы (план 22, этап 4.4).
/// <para>
/// Извлечено из <c>SqliteFactStore</c>: каждый репозиторий хранилища использует
/// этот компонент, чтобы применить WAL + миграции один раз (потокобезопасно),
/// а не дублировать логику. <see cref="InitializeAsync"/> — публичный прогревочный
/// метод для тестов.
/// </para>
/// </summary>
public sealed class SqliteSchemaInitializer
{
    private readonly string _connectionString;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    public SqliteSchemaInitializer(string databasePath)
    {
        _connectionString = MigrationRunner.BuildConnectionString(databasePath);
    }

    /// <summary>Строка подключения к хранилищу.</summary>
    public string ConnectionString => _connectionString;

    /// <summary>Прогрев схемы (WAL + миграции). Идемпотентен и потокобезопасен.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => EnsureInitializedAsync(ct);

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await new MigrationRunner().ApplyAsync(_connectionString, ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }
}