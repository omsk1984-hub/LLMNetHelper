using System.Data;
using LLMNetHelper.Abstractions;
using Microsoft.Data.Sqlite;

namespace LLMNetHelper.Core.Storage;

/// <summary>
/// SQLite-хранилище фактов: сохранение, чтение, очистка по retention.
/// Использует WAL-режим для лучшей конкурентности чтения.
/// </summary>
public sealed class SqliteFactStore : IFactStore
{
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

    private readonly string _connectionString;
    private readonly string _migrationSql;

    public SqliteFactStore(string databasePath)
    {
        EnsureProvider();
        _connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared";
        _migrationSql = LoadMigrationSql();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Включаем WAL-режим
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        await walCmd.ExecuteNonQueryAsync(ct);

        // Применяем миграции
        using var migrateCmd = conn.CreateCommand();
        migrateCmd.CommandText = _migrationSql;
        await migrateCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CreateRunAsync(string windowFrom, string windowTo, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO runs (started_at, window_from, window_to, status)
            VALUES (@started_at, @window_from, @window_to, 'in_progress');
            SELECT last_insert_rowid();";

        cmd.Parameters.AddWithValue("@started_at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@window_from", windowFrom);
        cmd.Parameters.AddWithValue("@window_to", windowTo);

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result!);
    }

    public async Task FinishRunAsync(
        long runId, string status, Verdict? verdict, string? error, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE runs
            SET finished_at = @finished_at, status = @status, verdict = @verdict, error = @error
            WHERE id = @run_id;";

        cmd.Parameters.AddWithValue("@finished_at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        // План 22, этап 1.1: enum → прежняя wire-строка в колонке runs.verdict.
        cmd.Parameters.AddWithValue("@verdict",
            verdict is { } v ? VerdictFormat.ToWire(v) : DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@run_id", runId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveLogFactsAsync(long runId, IEnumerable<LogFact> facts, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var fact in facts)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO log_facts
                        (run_id, bucket_start, job, svc, lvl, logger, symbol, count)
                    VALUES (@run_id, @bucket, @job, @svc, @lvl, @logger, @symbol, @count);";

                cmd.Parameters.AddWithValue("@run_id", runId);
                cmd.Parameters.AddWithValue("@bucket", fact.BucketStart);
                cmd.Parameters.AddWithValue("@job", (object?)fact.Job ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@svc", (object?)fact.Svc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@lvl", (object?)fact.Lvl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@logger", (object?)fact.Logger ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@symbol", (object?)fact.Symbol ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@count", fact.Count);

                await cmd.ExecuteNonQueryAsync(ct);
                cmd.Parameters.Clear();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SaveErrorSamplesAsync(long runId, IEnumerable<ErrorSample> samples, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var sample in samples)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO error_samples
                        (run_id, signature, lvl, job, logger, symbol, first_seen, last_seen, count, sample_text)
                    VALUES (@run_id, @sig, @lvl, @job, @logger, @symbol, @first, @last, @count, @text);";

                cmd.Parameters.AddWithValue("@run_id", runId);
                cmd.Parameters.AddWithValue("@sig", sample.Signature);
                cmd.Parameters.AddWithValue("@lvl", sample.Lvl);
                cmd.Parameters.AddWithValue("@job", (object?)sample.Job ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@logger", (object?)sample.Logger ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@symbol", (object?)sample.Symbol ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@first", sample.FirstSeen);
                cmd.Parameters.AddWithValue("@last", sample.LastSeen);
                cmd.Parameters.AddWithValue("@count", sample.Count);
                cmd.Parameters.AddWithValue("@text", sample.SampleText);

                await cmd.ExecuteNonQueryAsync(ct);
                cmd.Parameters.Clear();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SaveMetricFactsAsync(long runId, IEnumerable<MetricFact> facts, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var fact in facts)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO metric_facts
                        (run_id, name, labels, value, slope_per_hour, r2, window_from, window_to)
                    VALUES (@run_id, @name, @labels, @value, @slope, @r2, @wf, @wt);";

                cmd.Parameters.AddWithValue("@run_id", runId);
                cmd.Parameters.AddWithValue("@name", fact.Name);
                cmd.Parameters.AddWithValue("@labels", (object?)fact.Labels ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@value", fact.Value);
                cmd.Parameters.AddWithValue("@slope", fact.SlopePerHour);
                cmd.Parameters.AddWithValue("@r2", fact.R2);
                cmd.Parameters.AddWithValue("@wf", (object?)fact.WindowFrom ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@wt", (object?)fact.WindowTo ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct);
                cmd.Parameters.Clear();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task SaveLlmRunAsync(long runId, LlmRunInfo info, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO llm_runs
                (run_id, provider, endpoint, model, prompt_tokens, completion_tokens,
                 duration_ms, finish_reason, fallback_used, raw_response)
            VALUES (@run_id, @provider, @endpoint, @model, @pt, @ct, @dur, @fr, @fb, @raw);";

        cmd.Parameters.AddWithValue("@run_id", runId);
        cmd.Parameters.AddWithValue("@provider", info.Provider);
        cmd.Parameters.AddWithValue("@endpoint", info.Endpoint != null ? (object)info.Endpoint : DBNull.Value);
        cmd.Parameters.AddWithValue("@model", info.Model != null ? (object)info.Model : DBNull.Value);
        cmd.Parameters.AddWithValue("@pt", info.PromptTokens.HasValue ? (object)info.PromptTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@ct", info.CompletionTokens.HasValue ? (object)info.CompletionTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", info.DurationMs.HasValue ? (object)info.DurationMs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@fr", info.FinishReason != null ? (object)info.FinishReason : DBNull.Value);
        cmd.Parameters.AddWithValue("@fb", info.FallbackUsed ? 1 : 0);
        cmd.Parameters.AddWithValue("@raw", info.RawResponse != null ? (object)info.RawResponse : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RunInfo?> GetLastSuccessfulRunAsync(int hoursBack, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, started_at, finished_at, window_from, window_to, status, verdict, llm_provider, error
            FROM runs
            WHERE status IN ('ok', 'partial')
              AND started_at >= datetime('now', '-' || @hours || ' hours')
            ORDER BY started_at DESC
            LIMIT 1;";

        cmd.Parameters.AddWithValue("@hours", hoursBack);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new RunInfo
            {
                Id = reader.GetInt64(0),
                StartedAt = reader.GetString(1),
                FinishedAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                WindowFrom = reader.GetString(3),
                WindowTo = reader.GetString(4),
                Status = reader.GetString(5),
                // План 22, этап 1.1: wire-строка → enum на выходе хранилища.
                Verdict = reader.IsDBNull(6) ? null : VerdictFormat.Parse(reader.GetString(6)),
                LlmProvider = reader.IsDBNull(7) ? null : reader.GetString(7),
                Error = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }
        return null;
    }

    public async Task<List<ErrorSample>> GetPreviousErrorSamplesAsync(long runId, CancellationToken ct = default)
    {
        var samples = new List<ErrorSample>();

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT signature, lvl, job, logger, symbol, first_seen, last_seen, count, sample_text
            FROM error_samples
            WHERE run_id = @run_id
            ORDER BY count DESC;";

        cmd.Parameters.AddWithValue("@run_id", runId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new ErrorSample
            {
                Signature = reader.GetString(0),
                Lvl = reader.GetString(1),
                Job = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Logger = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Symbol = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FirstSeen = reader.GetString(5),
                LastSeen = reader.GetString(6),
                Count = reader.GetInt32(7),
                SampleText = reader.GetString(8)
            });
        }
        return samples;
    }

    public async Task<List<LogFact>> GetPreviousLogFactsAsync(long runId, CancellationToken ct = default)
    {
        var facts = new List<LogFact>();

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT bucket_start, job, svc, lvl, logger, symbol, count
            FROM log_facts
            WHERE run_id = @run_id;";

        cmd.Parameters.AddWithValue("@run_id", runId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(new LogFact
            {
                BucketStart = reader.GetString(0),
                Job = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Svc = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Lvl = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Logger = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Symbol = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Count = reader.GetInt32(6)
            });
        }
        return facts;
    }

    public async Task<int> PurgeOldRunsAsync(int retentionDays, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Границу считаем в .NET по UTC и передаём как ISO-строку.
        // datetime('now', '-0 days') в SQLite — это начало текущих суток, а не «сейчас»,
        // из-за чего записи старше N дней, но моложе начала сегодня, не удалялись.
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");

        // Каскадная очистка дочерних таблиц: FK-ограничения в SQLite не включены,
        // поэтому удаляем явно, иначе остаются «сироты».
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM llm_runs     WHERE run_id IN (SELECT id FROM runs WHERE started_at < @cutoff);
            DELETE FROM metric_facts WHERE run_id IN (SELECT id FROM runs WHERE started_at < @cutoff);
            DELETE FROM error_samples WHERE run_id IN (SELECT id FROM runs WHERE started_at < @cutoff);
            DELETE FROM log_facts    WHERE run_id IN (SELECT id FROM runs WHERE started_at < @cutoff);
            DELETE FROM runs         WHERE started_at < @cutoff;";

        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        // SQLite connection disposed with each using block
    }

    private static string LoadMigrationSql()
    {
        var assembly = typeof(SqliteFactStore).Assembly;
        using var stream = assembly.GetManifestResourceStream("LLMNetHelper.Core.Storage.Migrations.001_init.sql");
        if (stream == null)
            throw new InvalidOperationException("Migration file 001_init.sql not found as embedded resource.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
