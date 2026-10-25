using System.Text;
using LLMNetHelper.Abstractions;
using LLMNetHelper.Chat;
using Xunit;

namespace LLMNetHelper.Tests.Chat;

/// <summary>
/// Тесты диспетчера команд чата (план 22, этап 4.2): логика, вынесенная из
/// <see cref="ChatSession"/>. Проверяем отсутствие регрессий в вызовах
/// транскрипта и выводе — используется фейковый контекст.
/// </summary>
public class ChatCommandDispatcherTests
{
    private readonly StringBuilder _output = new();
    private readonly RecordingTranscript _transcript = new();

    private FakeContext CreateContext() => new(_output, _transcript);

    [Fact]
    public async Task Dispatch_Help_LogsCommandAndPrintsHelp()
    {
        var ctx = CreateContext();
        await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("/help"), "/help", ctx, CancellationToken.None);

        Assert.Contains("справка", _output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LogCommand:/help", _transcript.Events);
    }

    [Fact]
    public async Task Dispatch_Exit_ReturnsExitZero()
    {
        var ctx = CreateContext();
        var result = await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("/exit"), "/exit", ctx, CancellationToken.None);

        Assert.True(result.Exit);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("LogCommand:/exit", _transcript.Events);
    }

    [Fact]
    public async Task Dispatch_Message_EmptyArgument_Skips()
    {
        var ctx = CreateContext();
        var result = await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("   "), "   ", ctx, CancellationToken.None);

        Assert.False(result.Exit);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public async Task Dispatch_Message_AnswersAndPrints()
    {
        var ctx = CreateContext();
        ctx.Answer = "ответ";
        var result = await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("вопрос"), "вопрос", ctx, CancellationToken.None);

        Assert.Equal("вопрос", ctx.AskedQuestion);
        Assert.Contains("ответ", _output.ToString());
    }

    [Fact]
    public async Task Dispatch_Unknown_PrintsAndLogs()
    {
        var ctx = CreateContext();
        await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("/nonexistent"), "/nonexistent", ctx, CancellationToken.None);

        Assert.Contains("Неизвестная команда", _output.ToString());
        Assert.Contains("LogCommand:/nonexistent", _transcript.Events);
    }

    [Fact]
    public async Task Dispatch_Tools_TogglesAndLogs()
    {
        var ctx = CreateContext();
        Assert.False(ctx.ToolsMode);

        await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("/tools"), "/tools", ctx, CancellationToken.None);

        Assert.True(ctx.ToolsMode);
        Assert.Contains("LogNote:[режим] /tools (ReAct)", _transcript.Events);

        await ChatCommandDispatcher.DispatchAsync(
            ChatCommandParser.Parse("/tools"), "/tools", ctx, CancellationToken.None);
        Assert.False(ctx.ToolsMode);
    }

    private sealed class FakeContext(StringBuilder output, RecordingTranscript transcript) : IChatCommandContext
    {
        public ChatScope Scope { get; } = null!;
        public TextWriter Out { get; } = new StringWriter(output);
        public IChatTranscript Transcript => transcript;
        public bool ToolsMode { get; set; }
        public string Answer { get; set; } = "";
        public string? AskedQuestion { get; private set; }

        public void ClearHistory()
        {
        }

        public void PrintHelp() => Out.WriteLine("— справка");

        public void PrintRepos() => Out.WriteLine("— репозитории");

        public void PrintStatus() => Out.WriteLine("— статус");

        public Task<string> AnswerAsync(string question, CancellationToken ct)
        {
            AskedQuestion = question;
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingTranscript : IChatTranscript
    {
        public List<string> Events { get; } = [];

        public void LogUserMessage(string text) => Events.Add($"LogUserMessage:{text}");
        public void LogCommand(string commandLine) => Events.Add($"LogCommand:{commandLine}");
        public void LogModelStep(int step, string rawResponseText, int? inputTokens = null, int? outputTokens = null) => Events.Add("LogModelStep");
        public void LogToolCall(int step, string toolName, string argumentsJson) => Events.Add("LogToolCall");
        public void LogToolResult(int step, string toolName, string resultText, bool succeeded) => Events.Add("LogToolResult");
        public void LogAssistantAnswer(string text, int? inputTokens = null, int? outputTokens = null) => Events.Add("LogAssistantAnswer");
        public void LogNote(string text) => Events.Add($"LogNote:{text}");
        public void LogContextDocuments(IReadOnlyList<ContextDocument> documents) => Events.Add("LogContextDocuments");
    }
}