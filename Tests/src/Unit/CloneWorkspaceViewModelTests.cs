namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class CloneWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing work = new() { Path = "/work", Parent = "/", Folders = [] };

    public CloneWorkspaceViewModelTests()
    {
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(work);
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { DefaultService = AiService.Local });
    }

    [Fact]
    public async Task Cancel_RaisesCancelled()
    {
        CloneWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;
        await model.Open();

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Clone_CreatesTheWorkspaceWithTheDefaultProviderOfTheEnvironmentUnlessAnotherIsChosen()
    {
        WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Codex };
        connection.Setup(x => x.CloneWorkspace("https://x/app.git", "/work", null, AiService.Codex, "sk-9", It.IsAny<CancellationToken>())).ReturnsAsync(created);
        CloneWorkspaceViewModel model = new(connection.Object);
        WorkspaceInfo? raised = null;
        model.Created += workspace => raised = workspace;
        await model.Open();
        Assert.Equal(AiService.Local, model.Provider.Service);
        model.Provider.SelectedService = model.Provider.Services.Single(choice => choice.Service == AiService.Codex);
        model.Provider.ApiKey = "sk-9";
        model.Url = " https://x/app.git ";

        model.CloneCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && raised is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(created, raised);
    }
}
