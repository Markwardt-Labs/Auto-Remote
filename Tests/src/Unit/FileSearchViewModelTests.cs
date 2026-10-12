namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FileSearchViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Query_SearchesByNameAndListsTheHits()
    {
        connection.Setup(x => x.SearchFiles(id, "cfg", false, true, It.IsAny<CancellationToken>())).ReturnsAsync([new FileSearchResult { Path = "src/cfg.cs" }]);
        FileSearchViewModel search = new(connection.Object, id, () => true) { IsOpen = true };

        search.Query = "cfg";
        await Until(() => search.Results.Count == 1);

        Assert.Equal("cfg.cs", search.Results[0].Primary);
        Assert.Equal("src", search.Results[0].Secondary);
        Assert.False(search.HasNoResults);
    }

    [Fact]
    public async Task ContentMode_ShowsTheLineAndWaitsForAQuery()
    {
        connection.Setup(x => x.SearchFiles(id, "needle", true, false, It.IsAny<CancellationToken>())).ReturnsAsync([new FileSearchResult { Path = "a.txt", Line = 4, Text = "a needle" }]);
        FileSearchViewModel search = new(connection.Object, id, () => false) { IsOpen = true };

        search.SearchContent = true;
        await Task.Delay(300);
        connection.Verify(x => x.SearchFiles(id, string.Empty, true, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        search.Query = "needle";
        await Until(() => search.Results.Count == 1);

        Assert.Equal("a needle", search.Results[0].Primary);
        Assert.Equal("a.txt:4", search.Results[0].Secondary);
    }

    [Fact]
    public async Task NoHits_ReportsNoResults()
    {
        connection.Setup(x => x.SearchFiles(id, "zzz", false, false, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        FileSearchViewModel search = new(connection.Object, id, () => false) { IsOpen = true };

        search.Query = "zzz";
        await Until(() => search.HasNoResults);
    }

    [Fact]
    public void Choose_RaisesTheHitWithTheQuery()
    {
        FileSearchViewModel search = new(connection.Object, id, () => false) { IsOpen = true };
        FileSearchResult hit = new() { Path = "a.txt", Line = 2, Text = "x" };
        (FileSearchResult, string)? chosen = null;
        search.Chosen += (result, text) => chosen = (result, text);
        search.Query = "x";

        search.ChooseCommand.Execute(new FileSearchItem(hit));

        Assert.Equal((hit, "x"), chosen);
    }
}
