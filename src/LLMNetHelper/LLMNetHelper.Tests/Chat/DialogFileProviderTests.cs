using LLMNetHelper.Chat;
using Xunit;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты провайдера файла транскрипта (план 22, этап 4.7): уникальный путь
/// и создание UTF-8-писателя.
/// </summary>
public class DialogFileProviderTests
{
    [Fact]
    public void UniqueFilePath_Format_UtcStampedMd()
    {
        var utc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var path = DialogFileProvider.UniqueFilePath(@"C:\logs", utc);

        Assert.Equal(@"C:\logs\chat-2026-01-02_03-04-05Z.md", path);
    }

    [Fact]
    public void UniqueFilePath_Collision_Suffix()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialog-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var utc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var first = DialogFileProvider.UniqueFilePath(root, utc);
            File.WriteAllText(first, "x");

            var second = DialogFileProvider.UniqueFilePath(root, utc);

            Assert.Equal(Path.Combine(root, "chat-2026-01-02_03-04-05Z-2.md"), second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateWriter_CreatesDirectoryAndFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dialog-provider-{Guid.NewGuid():N}");
        try
        {
            var writer = DialogFileProvider.CreateWriter(root, out var filePath);

            Assert.True(File.Exists(filePath));
            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(filePath));
            writer.Dispose();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}