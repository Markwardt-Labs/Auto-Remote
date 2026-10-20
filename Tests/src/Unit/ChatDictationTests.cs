namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChatDictationTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IVoiceRecorder> recorder = new();

    private readonly ChatViewModel chat;

    public ChatDictationTests()
    {
        recorder.SetupGet(x => x.SampleRate).Returns(16000);
        recorder.Setup(x => x.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        recorder.Setup(x => x.Stop()).ReturnsAsync(Speech(1));
        connection.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("hello there");
        chat = new ChatViewModel(connection.Object, Guid.NewGuid(), null, recorder.Object);
    }

    private ReadOnlyMemory<byte> Speech(double seconds) => new byte[(int)(16000 * 2 * seconds)];

    [Fact]
    public void CanDictate_IsOnlyTrueWhenTheClientCanRecord()
    {
        Assert.True(chat.CanDictate);
        Assert.False(new ChatViewModel(connection.Object, Guid.NewGuid()).CanDictate);
    }

    [Fact]
    public void DictateCommand_RecordsThenTurnsTheSpeechIntoTextAddedToTheMessageBox()
    {
        ReadOnlyMemory<byte> sent = default;
        connection.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Callback((ReadOnlyMemory<byte> audio, CancellationToken _) => sent = audio).ReturnsAsync(" hello there ");

        chat.DictateCommand.Execute(null);

        Assert.True(chat.IsRecording);
        Assert.Equal("Stop recording and add the text", chat.DictateTip);

        chat.DictateCommand.Execute(null);

        Assert.False(chat.IsRecording);
        Assert.False(chat.IsTranscribing);
        Assert.Equal("hello there", chat.Input);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(sent.Span[..4]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(sent.Span[8..12]));
        Assert.Equal(44 + 32000, sent.Length);
        Assert.Equal(16000, BitConverter.ToInt32(sent.Span[24..28]));
        Assert.Equal(32000, BitConverter.ToInt32(sent.Span[40..44]));
        Assert.Null(chat.Error);
    }

    [Theory]
    [InlineData("", "hello there")]
    [InlineData("so far", "so far hello there")]
    [InlineData("so far ", "so far hello there")]
    [InlineData("so far\n", "so far\nhello there")]
    public void DictateCommand_AppendsToWhatWasAlreadyTyped(string typed, string expected)
    {
        chat.Input = typed;

        chat.DictateCommand.Execute(null);
        chat.DictateCommand.Execute(null);

        Assert.Equal(expected, chat.Input);
    }

    [Fact]
    public void DictateCommand_ReportsARecordingThatCouldNotStartAndDoesNotRecord()
    {
        recorder.Setup(x => x.Start(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("No microphone."));

        chat.DictateCommand.Execute(null);

        Assert.False(chat.IsRecording);
        Assert.Equal("No microphone.", chat.Error);
    }

    [Fact]
    public void DictateCommand_IgnoresARecordingTooShortToHoldSpeech()
    {
        recorder.Setup(x => x.Stop()).ReturnsAsync(Speech(0.1));

        chat.DictateCommand.Execute(null);
        chat.DictateCommand.Execute(null);

        Assert.Equal("Nothing was recorded.", chat.Error);
        Assert.False(chat.IsTranscribing);
        connection.Verify(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void DictateCommand_ReportsWhenNothingWasHeard()
    {
        connection.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("  ");

        chat.DictateCommand.Execute(null);
        chat.DictateCommand.Execute(null);

        Assert.Equal("Nothing was heard.", chat.Error);
        Assert.Equal(string.Empty, chat.Input);
    }

    [Fact]
    public void DictateCommand_ShowsWhyTheServerCouldNotTranscribe()
    {
        chat.Input = "keep";
        connection.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Voice input is not set up on the server."));

        chat.DictateCommand.Execute(null);
        chat.DictateCommand.Execute(null);

        Assert.Equal("Voice input is not set up on the server.", chat.Error);
        Assert.Equal("keep", chat.Input);
        Assert.False(chat.IsTranscribing);
        Assert.True(chat.DictateCommand.CanExecute(null));
    }

    [Fact]
    public void DictateCommand_IsUnavailableWhileTheMessageBoxIsBlockedButStillStopsARecording()
    {
        chat.BlockReason = "A script is running.";

        Assert.False(chat.DictateCommand.CanExecute(null));

        chat.BlockReason = null;
        chat.DictateCommand.Execute(null);
        chat.BlockReason = "A script is running.";

        Assert.True(chat.DictateCommand.CanExecute(null));
    }

    [Fact]
    public void DictateCommand_IsUnavailableWhileTheSpeechIsBeingTurnedIntoText()
    {
        TaskCompletionSource<string> pending = new();
        connection.Setup(x => x.Transcribe(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        chat.DictateCommand.Execute(null);

        chat.DictateCommand.Execute(null);

        Assert.True(chat.IsTranscribing);
        Assert.False(chat.DictateCommand.CanExecute(null));

        pending.SetResult("done");

        Assert.Equal("done", chat.Input);
        Assert.True(chat.DictateCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_ReleasesTheMicrophoneOfARecordingInProgress()
    {
        chat.DictateCommand.Execute(null);

        chat.Dispose();

        recorder.Verify(x => x.Stop(), Times.Once);
    }
}
