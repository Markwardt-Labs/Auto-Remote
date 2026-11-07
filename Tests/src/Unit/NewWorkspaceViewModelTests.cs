namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class NewWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing home = new()
    {
        Path = "/home/me",
        Parent = "/home",
        Folders = [new FolderEntry { Name = "code", Path = "/home/me/code" }],
    };

    public NewWorkspaceViewModelTests()
    {
        connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(home);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [new WorkspaceGroup { Id = Guid.NewGuid(), Name = "Work" }], Workspaces = [] });
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { DefaultService = AiService.Codex });
    }

    [Fact]
    public async Task Open_ShowsHomeFolder()
    {
        NewWorkspaceViewModel model = new(connection.Object);

        await model.Open();

        Assert.Equal("/home/me", model.Browser.CurrentPath);
        Assert.Equal(home.Folders, model.Browser.Folders);
        Assert.True(model.Browser.CanGoUp);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        NewWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Open_SelectsTheDefaultProviderOfTheEnvironment()
    {
        NewWorkspaceViewModel model = new(connection.Object);

        await model.Open();

        Assert.Equal(AiService.Codex, model.Provider.Service);
        Assert.Equal(["Claude Code", "OpenAI Codex", "Local models"], model.Provider.Services.Select(choice => choice.Name));
    }

    [Fact]
    public async Task Open_KeepsTheFirstProviderWhenTheEnvironmentCannotBeRead()
    {
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("offline"));
        NewWorkspaceViewModel model = new(connection.Object);

        await model.Open();

        Assert.Equal(AiService.ClaudeCode, model.Provider.Service);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_CreatesTheWorkspaceWithTheChosenProviderAndTheKeyForItsSession()
    {
        WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = "/home/me", Service = AiService.Codex };
        connection.Setup(x => x.CreateWorkspace("/home/me", AiService.Codex, "sk-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(created);
        NewWorkspaceViewModel model = new(connection.Object);
        WorkspaceInfo? raised = null;
        model.Created += workspace => raised = workspace;
        await model.Open();
        model.Provider.ApiKey = " sk-1 ";

        model.StartCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && raised is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(created, raised);
    }

    [Fact]
    public async Task Start_FilesTheWorkspaceInTheChosenGroupOrAtTheTopLevel()
    {
        Guid group = Guid.NewGuid();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [new WorkspaceGroup { Id = group, Name = "Work" }], Workspaces = [] });
        WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = "/home/me", GroupId = group };
        connection.Setup(x => x.CreateWorkspace("/home/me", It.IsAny<AiService?>(), null, group, It.IsAny<CancellationToken>())).ReturnsAsync(created);
        NewWorkspaceViewModel model = new(connection.Object);
        WorkspaceInfo? raised = null;
        model.Created += workspace => raised = workspace;
        await model.Open();
        Assert.Equal(["Top level", "Work"], model.Group.Choices.Select(choice => choice.Name));
        Assert.Null(model.Group.GroupId);

        model.Group.Selected = model.Group.Choices[1];
        model.StartCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && raised is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(created, raised);
    }

    [Fact]
    public async Task Provider_OffersNoKeyFieldForLocalModelsAndSendsNoKeyForThem()
    {
        NewWorkspaceViewModel model = new(connection.Object);
        await model.Open();
        model.Provider.ApiKey = "sk-1";

        model.Provider.SelectedService = model.Provider.Services.Single(choice => choice.Service == AiService.Local);

        Assert.False(model.Provider.TakesApiKey);
        Assert.Null(model.Provider.Key);
        model.Provider.SelectedService = model.Provider.Services.Single(choice => choice.Service == AiService.ClaudeCode);
        Assert.True(model.Provider.TakesApiKey);
        Assert.Equal("sk-1", model.Provider.Key);
    }

    [Fact]
    public async Task Provider_SendsNoKeyWhenItIsBlank()
    {
        NewWorkspaceViewModel model = new(connection.Object);
        await model.Open();
        model.Provider.ApiKey = "   ";

        Assert.Null(model.Provider.Key);
    }
}
