namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task LoadUpdateStart_IsEmptyWithoutAFile()
    {
        SettingsStore store = new(temp.Combine("settings.json"));

        Assert.Null(await store.LoadUpdateStart(CancellationToken.None));
    }

    [Fact]
    public async Task SaveUpdateStart_KeepsTheTimeAcrossInstancesAndForgetsItOnNull()
    {
        string path = temp.Combine("sub/settings.json");
        DateTime start = new(2026, 11, 12, 11, 30, 15, DateTimeKind.Utc);
        await new SettingsStore(path).SaveUpdateStart(start, CancellationToken.None);

        SettingsStore reopened = new(path);

        Assert.Equal(start, await reopened.LoadUpdateStart(CancellationToken.None));

        await reopened.SaveUpdateStart(null, CancellationToken.None);
        await reopened.SaveUpdateStart(null, CancellationToken.None);

        Assert.Null(await reopened.LoadUpdateStart(CancellationToken.None));
    }

    [Fact]
    public async Task LoadUpdateStart_IgnoresAFileThatHoldsNoTime()
    {
        string path = temp.Combine("settings.json");
        await File.WriteAllTextAsync(path + ".update", "not a time");

        Assert.Null(await new SettingsStore(path).LoadUpdateStart(CancellationToken.None));
    }

    [Fact]
    public async Task SaveUpdateStart_WithNullDoesNotThrowWhenTheFileCannotBeRemoved()
    {
        string path = temp.Combine("settings.json");
        Directory.CreateDirectory(path + ".update");
        await File.WriteAllTextAsync(Path.Combine(path + ".update", "keep"), "x");

        await new SettingsStore(path).SaveUpdateStart(null, CancellationToken.None);

        Assert.Null(await new SettingsStore(path).LoadUpdateStart(CancellationToken.None));
    }
}
