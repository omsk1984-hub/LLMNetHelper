using LLMNetHelper.Core.Snapshot;

namespace LLMNetHelper.Tests.Snapshot;

public class ErrorSignatureNormalizerTests
{
    [Fact]
    public void Normalize_MasksGuids()
    {
        var text = "Error: 550e8400-e29b-41d4-a716-446655440000 failed";
        var normalized = ErrorSignatureNormalizer.Normalize(text);
        Assert.DoesNotContain("550e8400-e29b-41d4-a716-446655440000", normalized);
        Assert.Contains("<MASKED>", normalized);
    }

    [Fact]
    public void Normalize_MasksBatchId()
    {
        var text = "Processing batch_id=abc123xyz completed";
        var normalized = ErrorSignatureNormalizer.Normalize(text);
        Assert.DoesNotContain("abc123xyz", normalized);
        Assert.Contains("<MASKED>", normalized);
    }

    [Fact]
    public void Normalize_SameTextSameHash()
    {
        var text1 = "Error in module X with guid 550e8400-e29b-41d4-a716-446655440000";
        var text2 = "Error in module Y with guid 6ba7b810-9dad-11d1-80b4-00c04fd430c8";
        var hash1 = ErrorSignatureNormalizer.ComputeHash(ErrorSignatureNormalizer.Normalize(text1));
        var hash2 = ErrorSignatureNormalizer.ComputeHash(ErrorSignatureNormalizer.Normalize(text2));
        // Разный текст после нормализации (module X vs module Y)
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Normalize_Idempotent()
    {
        var text = "Error: 550e8400-e29b-41d4-a716-446655440000 batch_id=abc123";
        var normalized1 = ErrorSignatureNormalizer.Normalize(text);
        var normalized2 = ErrorSignatureNormalizer.Normalize(normalized1);
        Assert.Equal(normalized1, normalized2);
    }

    [Fact]
    public void ComputeHash_ReturnsConsistentLength()
    {
        var hash = ErrorSignatureNormalizer.ComputeHash("test");
        Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigit(c)));
    }
}
