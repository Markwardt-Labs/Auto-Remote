namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(path);

    [Fact]
    public async Task Load_ReadsPortAndKey()
    {
        await File.WriteAllTextAsync(path, """{ "port": 8123, "key": "abc" }""");

        ServerConfig config = await new ConfigLoader().Load(path, CancellationToken.None);

        Assert.Equal(8123, config.Port);
        Assert.Equal("abc", config.Key);
    }

    [Fact]
    public async Task Load_DefaultsThePort()
    {
        await File.WriteAllTextAsync(path, """{ "key": "abc" }""");

        ServerConfig config = await new ConfigLoader().Load(path, CancellationToken.None);

        Assert.Equal(52679, config.Port);
    }

    [Fact]
    public async Task Load_DefaultFolderDefaultsToHomeAndCanBeSet()
    {
        await File.WriteAllTextAsync(path, """{ "port": 8123, "key": "abc" }""");
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (await new ConfigLoader().Load(path, CancellationToken.None)).DefaultFolder);

        string folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { port = 8123, key = "abc", defaultFolder = folder }));
        Assert.Equal(folder, (await new ConfigLoader().Load(path, CancellationToken.None)).DefaultFolder);
    }

    [Fact]
    public async Task Load_RejectsMissingDefaultFolder()
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { port = 8123, key = "abc", defaultFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) }));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ConfigLoader().Load(path, CancellationToken.None));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{ "port": 8123 }""")]
    [InlineData("""{ "port": 0, "key": "abc" }""")]
    [InlineData("""{ "port": 70000, "key": "abc" }""")]
    [InlineData("""{ "port": 80, "key": "" }""")]
    public async Task Load_RejectsInvalidConfig(string json)
    {
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ConfigLoader().Load(path, CancellationToken.None));
    }

    [Fact]
    public async Task Load_ThrowsWhenFileMissing() => await Assert.ThrowsAsync<FileNotFoundException>(() => new ConfigLoader().Load(path, CancellationToken.None));
}
