using LLMNetHelper.Abstractions;
using LLMNetHelper.Core.Storage;
using Xunit;

namespace LLMNetHelper.Tests.Storage;

/// <summary>
/// Тесты типобезопасности дат (план 22, этап 1.5):
/// <see cref="IFactStore.CreateRunAsync"/> принимает <see cref="DateTime"/> (UTC),
/// метки времени в моделях — <see cref="DateTime"/> вместо строк;
/// конвертация в TEXT ISO-8601 инкапсулирована в <see cref="SqliteFactStore"/>,
/// формат БД не меняется.
/// </summary>
public class DateTimeRoundTripTests
{
    private static string GetTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"test_dt_rt_{Guid.NewGuid()}.db");

    /// <summary>
    /// CreateRunAsync(DateTime) + FinishRunAsync → GetLastSuccessfulRunAsync
    /// возвращает те же моменты времени (round-trip через TEXT-колонки).
    /// </summary>
    [Fact]
    public async Task CreateRun_DateTime_RoundTripsThroughStorage()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);

            var runId = await store.CreateRunAsync(from, to);
            await store.FinishRunAsync(runId, RunStatus.Ok, Verdict.Warning, null);

            var run = await store.GetLastSuccessfulRunAsync(48 * 365);

            Assert.NotNull(run);
            Assert.Equal(from, run!.WindowFrom);
            Assert.Equal(to, run.WindowTo);
            Assert.Equal(RunStatus.Ok, run.Status);
            Assert.Equal(Verdict.Warning, run.Verdict);
            // StartedAt — момент вставки, заполняется хранилищем (UTC).
            Assert.InRange(run.StartedAt, from, DateTime.UtcNow.AddMinutes(1));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// ErrorSample.FirstSeen/LastSeen — DateTime; round-trip через сохранение/чтение.
    /// </summary>
    [Fact]
    public async Task ErrorSample_DateTimes_RoundTripThroughStorage()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
            var runId = await store.CreateRunAsync(from, to);

            var firstSeen = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
            var lastSeen = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);

            await store.SaveErrorSamplesAsync(runId,
            [
                new ErrorSample
                {
                    Signature = "sig1",
                    Lvl = "fail",
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen,
                    Count = 3,
                    SampleText = "boom"
                }
            ]);

            var loaded = await store.GetPreviousErrorSamplesAsync(runId);

            var sample = Assert.Single(loaded);
            Assert.Equal(firstSeen, sample.FirstSeen);
            Assert.Equal(lastSeen, sample.LastSeen);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// LogFact.BucketStart — DateTime; round-trip через сохранение/чтение.
    /// </summary>
    [Fact]
    public async Task LogFact_BucketStart_RoundTripsThroughStorage()
    {
        var dbPath = GetTempDbPath();
        try
        {
            var store = new SqliteFactStore(dbPath);
            await store.InitializeAsync();

            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
            var runId = await store.CreateRunAsync(from, to);

            var bucket = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);

            await store.SaveLogFactsAsync(runId,
            [
                new LogFact { BucketStart = bucket, Job = "artas_writer", Lvl = "fail", Count = 5 }
            ]);

            var loaded = await store.GetPreviousLogFactsAsync(runId);

            var fact = Assert.Single(loaded);
            Assert.Equal(bucket, fact.BucketStart);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
