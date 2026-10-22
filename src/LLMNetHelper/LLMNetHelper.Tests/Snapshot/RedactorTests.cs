using LLMNetHelper.Core.Snapshot;

namespace LLMNetHelper.Tests.Snapshot;

public class RedactorTests
{
    [Fact]
    public void Redact_RemovesTapiApiKey()
    {
        var text = "Token: tapi_sk_abc123def456 used for auth";
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain("tapi_sk_abc123def456", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_RemovesPassword()
    {
        var text = "Connection string: Server=db;Password=secret123;";
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain("secret123", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_RemovesBearerToken()
    {
        var text = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_NoOp_WhenNoSecrets()
    {
        var text = "This is a normal log line with no secrets";
        var redacted = Redactor.Redact(text);
        Assert.Equal(text, redacted);
    }

    [Fact]
    public void Redact_HandlesNullInput()
    {
        var result = Redactor.Redact(null!);
        Assert.Equal("", result);
    }
}
