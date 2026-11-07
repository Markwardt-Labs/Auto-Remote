namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class StoredFileTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly List<int> ran = [];

    private string Path => temp.Combine("data/file.json");

    private StoredFile File(bool isPrivate = false) => new(
    [
        content =>
        {
            ran.Add(0);
            content["first"] = true;
        },
        content =>
        {
            ran.Add(1);
            content["second"] = content["first"]!.GetValue<bool>();
            content.Remove("old");
        }
    ], isPrivate);

    private async Task Store(string text)
    {
        Directory.CreateDirectory(temp.Combine("data"));
        await System.IO.File.WriteAllTextAsync(Path, text);
    }

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task Read_OfAMissingFileIsNull() => Assert.Null(await File().Read(Path, CancellationToken.None));

    [Fact]
    public async Task Read_TakesAFileWithoutAVersionForTheInitialVersionUpgradesItThroughEveryStepAndStoresTheResultAfterKeepingTheOriginal()
    {
        await Store("""{ "old": 1, "kept": "yes" }""");

        JsonObject? content = await File().Read(Path, CancellationToken.None);

        Assert.Equal([0, 1], ran);
        Assert.Equal(3, content!["version"]!.GetValue<int>());
        Assert.Equal(("yes", true, true), (content["kept"]!.GetValue<string>(), content["first"]!.GetValue<bool>(), content["second"]!.GetValue<bool>()));
        Assert.Null(content["old"]);
        Assert.Equal("""{ "old": 1, "kept": "yes" }""", await System.IO.File.ReadAllTextAsync(Path + ".v1.bak"));
        JsonObject stored = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path))!.AsObject();
        Assert.Equal("version", stored.First().Key);
        Assert.Equal(3, stored["version"]!.GetValue<int>());
        Assert.False(System.IO.File.Exists(Path + ".tmp"));
    }

    [Fact]
    public async Task Read_RunsOnlyTheStepsAFileOfAnEarlierVersionStillNeeds()
    {
        await Store("""{ "version": 2, "first": true }""");

        JsonObject? content = await File().Read(Path, CancellationToken.None);

        Assert.Equal([1], ran);
        Assert.Equal(3, content!["version"]!.GetValue<int>());
        Assert.True(System.IO.File.Exists(Path + ".v2.bak"));
        Assert.False(System.IO.File.Exists(Path + ".v1.bak"));
    }

    [Fact]
    public async Task Read_LeavesAFileOfTheCurrentVersionAsItIs()
    {
        await Store("""{ "version": 3, "x": 1 }""");

        JsonObject? content = await File().Read(Path, CancellationToken.None);

        Assert.Empty(ran);
        Assert.Equal(1, content!["x"]!.GetValue<int>());
        Assert.Equal("""{ "version": 3, "x": 1 }""", await System.IO.File.ReadAllTextAsync(Path));
        Assert.Empty(Directory.GetFiles(temp.Combine("data"), "*.bak"));
    }

    [Fact]
    public async Task Read_RefusesAFileANewerReleaseWroteAndLeavesItAlone()
    {
        await Store("""{ "version": 4 }""");

        NewerFileException exception = await Assert.ThrowsAsync<NewerFileException>(() => File().Read(Path, CancellationToken.None));

        Assert.Contains("newer release", exception.Message);
        Assert.Equal("""{ "version": 4 }""", await System.IO.File.ReadAllTextAsync(Path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "version": 0 }""")]
    [InlineData("""{ "version": -3 }""")]
    public async Task Read_RefusesAFileThatIsNotAJsonObject(string text)
    {
        await Store(text);

        await Assert.ThrowsAsync<InvalidDataException>(() => File().Read(Path, CancellationToken.None));
    }

    [Fact]
    public async Task Read_DoesNotOverwriteABackupThatIsAlreadyThere()
    {
        await Store("""{ "old": 1 }""");
        await System.IO.File.WriteAllTextAsync(Path + ".v1.bak", "first backup");

        await File().Read(Path, CancellationToken.None);

        Assert.Equal("first backup", await System.IO.File.ReadAllTextAsync(Path + ".v1.bak"));
    }

    [Fact]
    public async Task Write_StampsTheVersionFirstWithoutChangingTheContentGiven()
    {
        JsonObject given = new() { ["name"] = "x", ["version"] = 99 };

        await File().Write(Path, given, CancellationToken.None);

        JsonObject stored = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path))!.AsObject();
        Assert.Equal(["version", "name"], stored.Select(pair => pair.Key));
        Assert.Equal(3, stored["version"]!.GetValue<int>());
        Assert.Equal(99, given["version"]!.GetValue<int>());
        Assert.Empty(ran);
        Assert.False(System.IO.File.Exists(Path + ".tmp"));
    }

    [Fact]
    public async Task Write_OfAPrivateFileIsReadableByItsOwnerOnly()
    {
        await File(true).Write(Path, new JsonObject(), CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(Path));
        }
    }

    [Fact]
    public async Task WrittenContent_ReadsBackWithoutUpgrading()
    {
        StoredFile file = File();
        await file.Write(Path, new JsonObject { ["x"] = 1 }, CancellationToken.None);

        JsonObject? content = await file.Read(Path, CancellationToken.None);

        Assert.Empty(ran);
        Assert.Equal(1, content!["x"]!.GetValue<int>());
    }
}
