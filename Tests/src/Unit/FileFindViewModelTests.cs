namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FileFindViewModelTests
{
    private FileFindViewModel Open(string text, string query)
    {
        FileFindViewModel find = new();
        find.Show(text, true);
        find.IsOpen = true;
        find.Query = query;
        return find;
    }

    [Fact]
    public void Query_FindsEveryMatchIgnoringCaseAndGoesToTheFirst()
    {
        FileFindViewModel find = Open("Foo foo FOO bar", "foo");

        Assert.Equal([0, 4, 8], find.Matches.Starts);
        Assert.Equal(0, find.Matches.Current);
        Assert.True(find.Matches.Navigate);
        Assert.Equal("1 of 3", find.Display);
    }

    [Fact]
    public void MatchCase_AndWholeWord_NarrowTheMatches()
    {
        FileFindViewModel find = Open("Foo foo food", "foo");

        find.MatchCase = true;
        Assert.Equal([4, 8], find.Matches.Starts);
        find.WholeWord = true;
        Assert.Equal([4], find.Matches.Starts);
    }

    [Fact]
    public void NextAndPrevious_CycleThroughTheMatches()
    {
        FileFindViewModel find = Open("a a a", "a");

        find.NextCommand.Execute(null);
        Assert.Equal(1, find.Matches.Current);
        find.PreviousCommand.Execute(null);
        find.PreviousCommand.Execute(null);
        Assert.Equal(2, find.Matches.Current);
        Assert.Equal("3 of 3", find.Display);
    }

    [Fact]
    public void Display_SaysWhenNothingMatchesAndIsEmptyWithoutAQuery()
    {
        FileFindViewModel find = Open("abc", "x");

        Assert.Equal("No results", find.Display);
        Assert.False(find.NextCommand.CanExecute(null));
        find.Query = string.Empty;
        Assert.Equal(string.Empty, find.Display);
    }

    [Fact]
    public void Show_KeepsTheCurrentMatchWhenTheTextChangesButNotWhenAnotherFileOpens()
    {
        FileFindViewModel find = Open("a a a", "a");
        find.NextCommand.Execute(null);

        find.Show("a a a a", false);
        Assert.Equal(1, find.Matches.Current);
        Assert.False(find.Matches.Navigate);
        find.Show("a a", true);
        Assert.Equal(0, find.Matches.Current);
    }

    [Fact]
    public void Close_ClearsTheMatches()
    {
        FileFindViewModel find = Open("abc", "b");

        find.CloseCommand.Execute(null);

        Assert.False(find.IsOpen);
        Assert.Empty(find.Matches.Starts);
    }
}
