using LLMNetHelper.Chat;

namespace LLMNetHelper.Tests.Chat;

public class ChatCommandParserTests
{
    [Theory]
    [InlineData("/help", ChatCommandKind.Help, "")]
    [InlineData("/?", ChatCommandKind.Help, "")]
    [InlineData("/HELP", ChatCommandKind.Help, "")]
    [InlineData("/exit", ChatCommandKind.Exit, "")]
    [InlineData("/quit", ChatCommandKind.Exit, "")]
    [InlineData("/repos", ChatCommandKind.Repos, "")]
    [InlineData("/clear", ChatCommandKind.Clear, "")]
    [InlineData("/status", ChatCommandKind.Status, "")]
    [InlineData("/tools", ChatCommandKind.Tools, "")]
    public void Parse_KnownCommands(string input, ChatCommandKind expectedKind, string expectedArg)
    {
        var cmd = ChatCommandParser.Parse(input);

        Assert.Equal(expectedKind, cmd.Kind);
        Assert.Equal(expectedArg, cmd.Argument);
    }

    [Fact]
    public void Parse_RepoWithName()
    {
        var cmd = ChatCommandParser.Parse("/repo MyService");

        Assert.Equal(ChatCommandKind.Repo, cmd.Kind);
        Assert.Equal("MyService", cmd.Argument);
    }

    [Fact]
    public void Parse_RepoWithoutName_ResetsScope()
    {
        var cmd = ChatCommandParser.Parse("/repo");

        Assert.Equal(ChatCommandKind.Repo, cmd.Kind);
        Assert.Equal("", cmd.Argument);
    }

    [Fact]
    public void Parse_RepoWithSpaces_TrimsArgument()
    {
        var cmd = ChatCommandParser.Parse("  /repo   MyService  ");

        Assert.Equal(ChatCommandKind.Repo, cmd.Kind);
        Assert.Equal("MyService", cmd.Argument);
    }

    [Fact]
    public void Parse_UnknownCommand_KeepsWholeInputAsArgument()
    {
        var cmd = ChatCommandParser.Parse("/frobnicate now");

        Assert.Equal(ChatCommandKind.Unknown, cmd.Kind);
        Assert.Equal("/frobnicate now", cmd.Argument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyInput_IsEmptyMessage(string? input)
    {
        var cmd = ChatCommandParser.Parse(input);

        Assert.Equal(ChatCommandKind.Message, cmd.Kind);
        Assert.Equal(string.Empty, cmd.Argument);
    }

    [Fact]
    public void Parse_PlainQuestion_IsMessage()
    {
        var cmd = ChatCommandParser.Parse("Как устроен Writer?");

        Assert.Equal(ChatCommandKind.Message, cmd.Kind);
        Assert.Equal("Как устроен Writer?", cmd.Argument);
    }

    [Fact]
    public void Parse_QuestionStartingWithSlashText_IsMessage()
    {
        // «/dev/null» — не команда: первая буква после / не совпадает с известными.
        var cmd = ChatCommandParser.Parse("/dev/null что это");

        Assert.Equal(ChatCommandKind.Unknown, cmd.Kind);
    }
}
