namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class WhisperEngineTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly WhisperEngine engine = new();

    public void Dispose() => temp.Dispose();

    private void Use(string? model) => engine.Configure(new ServerConfig { Port = 1, Key = "k", DataFolder = temp.Path, SpeechModel = model });

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task Prepare_WithoutAModelSaysVoiceInputIsTurnedOff(string? model)
    {
        Use(model);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => engine.Prepare(CancellationToken.None));

        Assert.Contains("speechModel", exception.Message);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public async Task Prepare_RefusesAModelNameThatIsNotAPlainName(string model)
    {
        Use(model);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => engine.Prepare(CancellationToken.None));

        Assert.Contains("neither a model file nor", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "models")));
    }

    [Fact]
    public async Task Prepare_UsesAModelFileThatExistsWithoutDownloadingAnything()
    {
        string file = temp.Combine("mine.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        Use(file);

        await engine.Prepare(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "models")));
    }

    [Fact]
    public async Task Prepare_DoesNotDownloadAgainWhenTheNamedModelIsAlreadyInTheModelsFolder()
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "models"));
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "models", "ggml-tiny.en.bin"), [1, 2, 3]);
        Use("tiny.en");

        await engine.Prepare(CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "models"), "*.download"));
    }

    [Fact]
    public async Task Transcribe_ReportsAModelFileThatCannotBeLoadedAndExtractsTheNativeLibrariesOnce()
    {
        string file = temp.Combine("broken.bin");
        await File.WriteAllBytesAsync(file, new byte[2048]);
        Use(file);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => engine.Transcribe(new byte[100], CancellationToken.None));

        Assert.Contains("broken.bin", exception.Message);
        Assert.NotEmpty(Directory.GetFiles(temp.Path, "*whisper*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
    }
}
