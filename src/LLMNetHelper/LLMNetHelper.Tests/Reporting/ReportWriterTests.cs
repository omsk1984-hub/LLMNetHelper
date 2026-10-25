using System.Text.Json;
using LLMNetHelper.Infrastructure.Reporting;

namespace LLMNetHelper.Tests.Reporting;

/// <summary>
/// Тесты контракта записи отчётов (<see cref="FileReportWriter"/>).
/// Сеть не используется; запись идёт во временный каталог.
/// </summary>
public class ReportWriterTests
{
    [Fact]
    public async Task WriteJsonAsync_GivenJsonString_WritesObjectNotQuotedString()
    {
        // Дефект D2 (план 18): GenerateReportStep передавал готовую JSON-строку
        // (результат JsonReportRenderer.Render) в WriteJsonAsync(object), которая
        // сериализовалась повторно — в файле оказывалась экранированная строка
        // вместо JSON-объекта, и отчёт не читался парсерами.
        // Контракт: WriteJsonAsync принимает ГОТОВУЮ JSON-строку и пишет её как есть.
        var dir = Path.Combine(Path.GetTempPath(), "llmnet-helper-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new FileReportWriter(dir);
            const string payload = "{\"run_id\":42,\"verdict\":\"OK\"}";

            await writer.WriteJsonAsync("report.json", payload, CancellationToken.None);

            var written = await File.ReadAllTextAsync(
                Path.Combine(dir, "report.json"), CancellationToken.None);

            // Ожидаем валидный JSON-объект, а не строку в кавычках.
            using var doc = JsonDocument.Parse(written);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal(42, doc.RootElement.GetProperty("run_id").GetInt32());
            Assert.Equal("OK", doc.RootElement.GetProperty("verdict").GetString());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
