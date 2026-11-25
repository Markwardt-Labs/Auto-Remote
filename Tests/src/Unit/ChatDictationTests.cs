namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChatDictationTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IVoiceRecorder> recorder = new();

    private readonly Guid workspaceId = Guid.NewGuid();

    private readonly ChatViewModel chat;

    public ChatDictationTests()
    {
        recorder.SetupGet(x => x.SampleRate).Returns(16000);
        recorder.Setup(x => x.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        recorder.Setup(x => x.Stop()).ReturnsAsync(Speech(1));
        connection.Setup(x => x.TranscribeSavedInput(workspaceId, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync([Converting()]);
        chat = new ChatViewModel(connection.Object, workspaceId, null, recorder.Object);
    }

    private ReadOnlyMemory<byte> Speech(double seconds) => new byte[(int)(16000 * 2 * seconds)];

    private SavedInput Converting() => new() { Id = Guid.NewGuid(), Text = string.Empty, Time = DateTimeOffset.UtcNow, IsConverting = true };

    [Fact]
    public void CanDictate_IsOnlyTrueWhenTheClientCanRecord()
    {
        Assert.True(chat.CanDictate);
        Assert.False(new ChatViewModel(connection.Object, Guid.NewGuid()).CanDictate);
    }

    [Fact]
    public void DictateCommand_StartsRecordingWhichShowsTheRecordingScreenAndLeavesTheMessageBoxAlone()
    {
        chat.Input = "typed";

        chat.DictateCommand.Execute(null);

        Assert.True(chat.IsRecording);
        Assert.Equal("0:00", chat.RecordingTime);
        Assert.False(chat.DictateCommand.CanExecute(null));
        Assert.True(chat.CompleteDictationCommand.CanExecute(null));
        Assert.True(chat.CancelDictationCommand.CanExecute(null));
        Assert.Equal("typed", chat.Input);
        recorder.Verify(x => x.Start(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void CompleteDictationCommand_SendsTheRecordingAsAWavAndOpensTheSavedInputsWithTheEntryBeingConverted()
    {
        ReadOnlyMemory<byte> sent = default;
        SavedInput entry = Converting();
        connection.Setup(x => x.TranscribeSavedInput(workspaceId, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Callback((Guid _, ReadOnlyMemory<byte> audio, CancellationToken _) => sent = audio).ReturnsAsync([entry]);
        chat.DictateCommand.Execute(null);

        chat.CompleteDictationCommand.Execute(null);

        Assert.False(chat.IsRecording);
        Assert.True(chat.IsSavedInputsOpen);
        Assert.Equal(entry, Assert.Single(chat.SavedInputs));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(sent.Span[..4]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(sent.Span[8..12]));
        Assert.Equal(44 + 32000, sent.Length);
        Assert.Equal(16000, BitConverter.ToInt32(sent.Span[24..28]));
        Assert.Equal(32000, BitConverter.ToInt32(sent.Span[40..44]));
        Assert.Null(chat.Error);
        Assert.True(chat.DictateCommand.CanExecute(null));
        Assert.Equal(string.Empty, chat.Input);
    }

    [Fact]
    public void CancelDictationCommand_ThrowsTheRecordingAwayWithoutSendingIt()
    {
        chat.DictateCommand.Execute(null);

        chat.CancelDictationCommand.Execute(null);

        Assert.False(chat.IsRecording);
        Assert.False(chat.IsSavedInputsOpen);
        Assert.Empty(chat.SavedInputs);
        recorder.Verify(x => x.Stop(), Times.Once);
        connection.Verify(x => x.TranscribeSavedInput(It.IsAny<Guid>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(chat.DictateCommand.CanExecute(null));
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
    public void CompleteDictationCommand_IgnoresARecordingTooShortToHoldSpeech()
    {
        recorder.Setup(x => x.Stop()).ReturnsAsync(Speech(0.1));
        chat.DictateCommand.Execute(null);

        chat.CompleteDictationCommand.Execute(null);

        Assert.Equal("Nothing was recorded.", chat.Error);
        Assert.False(chat.IsRecording);
        Assert.False(chat.IsSavedInputsOpen);
        connection.Verify(x => x.TranscribeSavedInput(It.IsAny<Guid>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void CompleteDictationCommand_ShowsWhyTheServerRefusedTheRecording()
    {
        chat.Input = "keep";
        connection.Setup(x => x.TranscribeSavedInput(workspaceId, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The recording is empty."));
        chat.DictateCommand.Execute(null);

        chat.CompleteDictationCommand.Execute(null);

        Assert.Equal("The recording is empty.", chat.Error);
        Assert.Equal("keep", chat.Input);
        Assert.False(chat.IsSavedInputsOpen);
        Assert.True(chat.DictateCommand.CanExecute(null));
    }

    [Fact]
    public void DictateCommand_IsUnavailableWhileTheMessageBoxIsBlockedButARecordingCanStillBeCompletedOrCancelled()
    {
        chat.BlockReason = "A script is running.";

        Assert.False(chat.DictateCommand.CanExecute(null));

        chat.BlockReason = null;
        chat.DictateCommand.Execute(null);
        chat.BlockReason = "A script is running.";

        Assert.True(chat.CompleteDictationCommand.CanExecute(null));
        Assert.True(chat.CancelDictationCommand.CanExecute(null));
    }

    [Fact]
    public void DictateCommand_IsUnavailableWhileTheFinishedRecordingIsBeingSent()
    {
        TaskCompletionSource<IReadOnlyList<SavedInput>> pending = new();
        connection.Setup(x => x.TranscribeSavedInput(workspaceId, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        chat.DictateCommand.Execute(null);

        chat.CompleteDictationCommand.Execute(null);

        Assert.False(chat.IsRecording);
        Assert.False(chat.DictateCommand.CanExecute(null));
        Assert.Equal("Sending the recording", chat.DictateTip);

        pending.SetResult([Converting()]);

        Assert.True(chat.DictateCommand.CanExecute(null));
        Assert.Equal("Voice input", chat.DictateTip);
    }

    [Fact]
    public void DiscardSavedInputCommand_CancelsAConversionOrDismissesAFailedOneAndShowsTheListTheServerAnswers()
    {
        SavedInput converting = Converting();
        SavedInput failed = Converting() with { IsConverting = false, Error = "Nothing was heard." };
        connection.Setup(x => x.DiscardSavedInput(workspaceId, converting.Id, It.IsAny<CancellationToken>())).ReturnsAsync([failed]);
        chat.DictateCommand.Execute(null);
        connection.Setup(x => x.TranscribeSavedInput(workspaceId, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync([converting, failed]);
        chat.CompleteDictationCommand.Execute(null);
        Assert.Equal(2, chat.SavedInputs.Count);

        chat.DiscardSavedInputCommand.Execute(converting);

        Assert.Equal(failed, Assert.Single(chat.SavedInputs));
        Assert.True(chat.DiscardSavedInputCommand.CanExecute(converting));
    }

    [Fact]
    public void LoadSavedInputCommand_IgnoresAnEntryThatIsStillBeingConvertedOrFailedAndStaysEnabledWhileATakeRuns()
    {
        TaskCompletionSource<SavedInput> pending = new();
        SavedInput ready = new() { Id = Guid.NewGuid(), Text = "ready", Time = DateTimeOffset.UtcNow };
        connection.Setup(x => x.TakeSavedInput(workspaceId, ready.Id, It.IsAny<CancellationToken>())).Returns(pending.Task);

        chat.LoadSavedInputCommand.Execute(Converting());
        chat.LoadSavedInputCommand.Execute(Converting() with { IsConverting = false, Error = "x" });
        chat.LoadSavedInputCommand.Execute(ready);
        chat.LoadSavedInputCommand.Execute(ready);

        Assert.True(chat.LoadSavedInputCommand.CanExecute(ready));
        connection.Verify(x => x.TakeSavedInput(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

        pending.SetResult(ready);

        Assert.Equal("ready", chat.Input);
    }

    [Fact]
    public void Dispose_ReleasesTheMicrophoneOfARecordingInProgress()
    {
        chat.DictateCommand.Execute(null);

        chat.Dispose();

        recorder.Verify(x => x.Stop(), Times.Once);
    }
}
