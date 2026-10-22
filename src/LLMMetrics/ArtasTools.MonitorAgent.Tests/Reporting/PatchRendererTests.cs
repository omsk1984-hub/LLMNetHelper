using ArtasTools.MonitorAgent.Core.Reporting;

namespace ArtasTools.MonitorAgent.Tests.Reporting;

public class PatchRendererTests
{
    [Fact]
    public void RenderFileDiff_NoChanges_ReturnsEmpty()
    {
        const string text = "line1\nline2";

        var diff = PatchRenderer.RenderFileDiff("src/A.cs", text, text);

        Assert.Equal("", diff);
    }

    [Fact]
    public void RenderFileDiff_AddedLine_HasPlusPrefix()
    {
        var original = "a\nb\nc";
        var patched = "a\nb\nnew\nc";

        var diff = PatchRenderer.RenderFileDiff("src/A.cs", original, patched);

        Assert.Contains("+new", diff);
        Assert.Contains("--- a/src/A.cs", diff);
        Assert.Contains("+++ b/src/A.cs", diff);
    }

    [Fact]
    public void RenderFileDiff_RemovedLine_HasMinusPrefix()
    {
        var original = "a\nb\nc";
        var patched = "a\nc";

        var diff = PatchRenderer.RenderFileDiff("src/A.cs", original, patched);

        Assert.Contains("-b", diff);
    }

    [Fact]
    public void RenderFileDiff_IncludesHunkHeader()
    {
        var diff = PatchRenderer.RenderFileDiff("A.cs", "a\nb", "a\nB");

        Assert.Contains("@@ -", diff);
        Assert.Contains(" +", diff);
        Assert.Contains(" @@", diff);
    }

    [Fact]
    public void RenderFileDiff_NormalizesWindowsPaths()
    {
        var diff = PatchRenderer.RenderFileDiff(@"src\Sub\A.cs", "a", "b");

        Assert.Contains("a/src/Sub/A.cs", diff);
        Assert.DoesNotContain(@"src\Sub", diff);
    }

    [Fact]
    public void RenderFileDiff_UsesContextLines()
    {
        var original = string.Join('\n', Enumerable.Range(0, 20).Select(static i => $"line{i}"));
        var patched = original.Replace("line10", "CHANGED");

        var diff = PatchRenderer.RenderFileDiff("A.cs", original, patched, contextLines: 2);

        // Изменение одно, значит хунк должен быть компактным, а не весь файл.
        Assert.Contains("CHANGED", diff);
        Assert.DoesNotContain("line0", diff);
        Assert.DoesNotContain("line19", diff);
    }

    [Fact]
    public void RenderFileDiff_EmptyOriginal_ShowsOnlyAdditions()
    {
        var diff = PatchRenderer.RenderFileDiff("A.cs", "", "a\nb");

        Assert.Contains("+a", diff);
        Assert.Contains("+b", diff);
    }

    [Fact]
    public void RenderFileDiff_EmptyPatched_ShowsOnlyRemovals()
    {
        var diff = PatchRenderer.RenderFileDiff("A.cs", "a\nb", "");

        Assert.Contains("-a", diff);
        Assert.Contains("-b", diff);
    }

    [Fact]
    public void RenderCombined_SkipsEmptyDiffs()
    {
        var combined = PatchRenderer.RenderCombined(
        [
            ("A.cs", "--- a/A.cs\n+++ b/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b"),
            ("B.cs", "")
        ]);

        Assert.Contains("# A.cs", combined);
        Assert.DoesNotContain("# B.cs", combined);
    }

    [Fact]
    public void RenderCombined_IncludesAllNonEmptyDiffs()
    {
        var combined = PatchRenderer.RenderCombined(
        [
            ("A.cs", "--- a/A.cs\n+++ b/A.cs"),
            ("B.cs", "--- a/B.cs\n+++ b/B.cs")
        ]);

        Assert.Contains("# A.cs", combined);
        Assert.Contains("# B.cs", combined);
    }
}
