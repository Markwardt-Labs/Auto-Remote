namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class SpeechServiceTests
{
    private readonly Mock<IWhisperEngine> engine = new();

    private readonly SpeechService service;

    public SpeechServiceTests() => service = new SpeechService(engine.Object);

    private string Audio(int bytes = 4) => Convert.ToBase64String(new byte[bytes].Select((_, i) => (byte)(i + 1)).ToArray());

    private void Use(string script) => service.Configure(new ServerConfig { Port = 1, Key = "k", SpeechCommand = "sh", SpeechArguments = ["-c", script, "speech", "{file}"] });

    [Fact]
    public async Task Transcribe_RunsTheSpeechCommandOnTheRecordingAndAnswersItsTrimmedOutput()
    {
        Use("echo \"  heard $(wc -c < \"$1\") bytes  \"");

        string text = await service.Transcribe(Audio(10), CancellationToken.None);

        Assert.Equal("heard 10 bytes", text);
    }

    [Fact]
    public async Task Transcribe_WithoutASpeechCommandUsesTheBuiltInEngineAndTrimsItsText()
    {
        ReadOnlyMemory<byte> heard = default;
        engine.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Callback((ReadOnlyMemory<byte> wav, CancellationToken _) => heard = wav).ReturnsAsync(" built in ");
        service.Configure(new ServerConfig { Port = 1, Key = "k" });

        string text = await service.Transcribe(Audio(6), CancellationToken.None);

        Assert.Equal("built in", text);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, heard.ToArray());
    }

    [Fact]
    public async Task Transcribe_WithASpeechCommandLeavesTheBuiltInEngineAlone()
    {
        Use("echo command");

        string text = await service.Transcribe(Audio(), CancellationToken.None);

        Assert.Equal("command", text);
        engine.Verify(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
        engine.Verify(x => x.Prepare(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Transcribe_PassesOnWhyTheBuiltInEngineCannotTranscribe()
    {
        engine.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("model missing"));
        service.Configure(new ServerConfig { Port = 1, Key = "k" });

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Transcribe(Audio(), CancellationToken.None));

        Assert.Equal("model missing", exception.Message);
    }

    [Fact]
    public void Configure_StartsDownloadingTheModelOnlyWhenTheBuiltInEngineIsUsed()
    {
        service.Configure(new ServerConfig { Port = 1, Key = "k" });
        service.Configure(new ServerConfig { Port = 1, Key = "k", SpeechModel = "" });
        service.Configure(new ServerConfig { Port = 1, Key = "k", SpeechCommand = "sh" });

        engine.Verify(x => x.Prepare(It.IsAny<CancellationToken>()), Times.Once);
        engine.Verify(x => x.Configure(It.IsAny<ServerConfig>()), Times.Exactly(3));
    }

    [Fact]
    public void Configure_IgnoresAFailedDownloadSoTheServerStillStarts()
    {
        engine.Setup(x => x.Prepare(It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("offline"));

        service.Configure(new ServerConfig { Port = 1, Key = "k" });
    }

    [Fact]
    public async Task Transcribe_ReportsWhyTheCommandFailed()
    {
        Use("echo nope >&2; exit 3");

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Transcribe(Audio(), CancellationToken.None));

        Assert.Contains("nope", exception.Message);
    }

    [Fact]
    public async Task Transcribe_ReportsACommandThatCannotBeStarted()
    {
        service.Configure(new ServerConfig { Port = 1, Key = "k", SpeechCommand = "/no/such/speech-command" });

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Transcribe(Audio(), CancellationToken.None));

        Assert.Contains("could not be started", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    public async Task Transcribe_RejectsARecordingThatIsEmptyOrNotBase64(string data)
    {
        Use("echo hi");

        await Assert.ThrowsAsync<RequestException>(() => service.Transcribe(data, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_RejectsARecordingAboveTheLimit()
    {
        Use("echo hi");

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Transcribe(Audio(5 * 1024 * 1024), CancellationToken.None));

        Assert.Contains("larger", exception.Message);
    }

    [Fact]
    public async Task Transcribe_EndsTheCommandAndLeavesNoFileBehindWhenCancelled()
    {
        using TempFolder temp = new();
        service.Configure(new ServerConfig { Port = 1, Key = "k", SpeechCommand = "sh", SpeechArguments = ["-c", "echo \"$1\" > \"$2\"; sleep 30", "speech", "{file}", temp.Combine("path")] });
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(700));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Transcribe(Audio(), cancel.Token));

        string recording = (await File.ReadAllTextAsync(temp.Combine("path"))).Trim();
        Assert.False(File.Exists(recording));
    }
}
