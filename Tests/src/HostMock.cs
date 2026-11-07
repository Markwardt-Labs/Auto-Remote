namespace Markwardt.AutoRemote.Tests;

/// <summary>
/// A mocked prompter host over a fake prompter whose sessions it makes the way the real host does, without the environment and the files; moving a session does nothing.
/// </summary>
internal sealed class HostMock
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HostMock" /> class.
    /// </summary>
    /// <param name="prompter">The fake prompter every session is made by.</param>
    public HostMock(FakePrompter prompter)
    {
        Prompter = prompter;
        Mock.Setup(x => x.Prompter).Returns(prompter);
        Mock.Setup(x => x.Get()).Returns(new AiEnvironment());
        Mock.Setup(x => x.Setup(It.IsAny<AiService>(), It.IsAny<string?>())).Returns((AiService service, string? key) => Setup(service, key));
        Mock.Setup(x => x.CreateSession(It.IsAny<string>(), It.IsAny<AiService>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(async (string folder, AiService service, string? key, CancellationToken cancellation) =>
        {
            await using IPromptSession session = await prompter.Create(Setup(service, key) with { WorkspacePath = folder }, cancellation);
            return session.Id;
        });
        Mock.Setup(x => x.ComposeInstructions(It.IsAny<string?>(), It.IsAny<int?>())).Returns((string? user, int? minutes) => BuiltIn.Compose(user, minutes ?? 5));
        Mock.Setup(x => x.StatusTool).Returns(BuiltIn.StatusTool);
        Mock.Setup(x => x.UserInstructions(It.IsAny<string?>())).Returns((string? stored) => BuiltIn.Visible(stored));
        Mock.Setup(x => x.Adopt(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock.Setup(x => x.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Gets the built-in instructions the mocked host puts in front of the user's, as the real one does.
    /// </summary>
    public BuiltInInstructions BuiltIn { get; } = new();

    /// <summary>
    /// Gets the mock, to set up and verify.
    /// </summary>
    public Mock<IPrompterHost> Mock { get; } = new();

    /// <summary>
    /// Gets the fake prompter.
    /// </summary>
    public FakePrompter Prompter { get; }

    /// <summary>
    /// Gets the host to give to what is tested.
    /// </summary>
    public IPrompterHost Object => Mock.Object;

    private PromptSetup Setup(AiService service, string? key)
        => service switch
        {
            AiService.Codex => new CodexSetup { ApiKey = key },
            AiService.Local => new LocalSetup(),
            _ => new ClaudeSetup { ApiKey = key }
        };
}
