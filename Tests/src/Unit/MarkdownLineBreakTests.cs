namespace Markwardt.AutoRemote.Tests.Unit;

[Collection("Headless")]
public sealed class MarkdownLineBreakTests(HeadlessFixture fixture)
{
    private async Task<List<string>> Pieces(string source) => await fixture.Run(() =>
    {
        MarkdownView view = new() { Source = source };
        List<string> pieces = [.. Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<TextBlock>().SelectMany(block => block.Inlines is { Count: > 0 } inlines ? inlines.Select(inline => inline is LineBreak ? "\n" : inline is Run run ? run.Text ?? string.Empty : string.Empty) : [block.Text ?? string.Empty])];
        return pieces;
    });

    [Theory]
    [InlineData("one<br>two")]
    [InlineData("one<br/>two")]
    [InlineData("one<br />two")]
    [InlineData("one<BR>two")]
    public async Task MarkdownView_BreaksTheLineAtABreakTagInsteadOfShowingTheTag(string source)
    {
        List<string> pieces = await Pieces(source);

        Assert.Equal(["one", "\n", "two"], pieces);
    }

    [Fact]
    public async Task MarkdownView_BreaksTheLinesOfATableCellAtBreakTags()
    {
        List<string> pieces = await Pieces("| a | b |\n| - | - |\n| first<br>second<br/>third | plain |");

        Assert.Equal(["a", "b", "first", "\n", "second", "\n", "third", "plain"], pieces);
    }

    [Fact]
    public async Task MarkdownView_KeepsABreakTagInsideCodeAndOtherHtmlTagsAsText()
    {
        List<string> pieces = await Pieces("`a<br>b` and <span>c</span>");

        Assert.Equal("a<br>b and <span>c</span>", string.Concat(pieces));
    }

    [Fact]
    public async Task MarkdownView_LeavesABreakTagOutOfTheTextFindSearchesAsALineBreak()
    {
        string plain = await fixture.Run(() => new MarkdownView { Source = "one<br>two" }.PlainText);

        Assert.Equal("one\ntwo\n", plain);
    }
}
