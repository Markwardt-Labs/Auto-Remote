namespace Markwardt.AutoRemote.Tests.Unit;

[Collection("Headless")]
public sealed class ViewTests(HeadlessFixture fixture)
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly string markdownSample = """
        # Title

        Some *italic*, **bold**, ~~gone~~, `code` and [a link](https://example.com).

        > quoted

        - one
        - [x] done
          - nested

        1. first
        2. second

        ```csharp
        int x = 1;
        ```

        | A | B |
        |---|---|
        | 1 | 2 |

        ---
        """;

    private string FlattenInlines(InlineCollection inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text,
        Span span => FlattenInlines(span.Inlines),
        _ => string.Empty,
    }));

    private async Task<(int Texts, int Grids, string Text)> RenderMarkdown(string? source) => await fixture.Run(() =>
    {
        MarkdownView view = new() { Source = source };
        Window window = new() { Width = 390, Height = 800, Content = view };
        window.Show();
        List<SelectableTextBlock> texts = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        int grids = view.GetVisualDescendants().OfType<Grid>().Count();
        string text = string.Join('|', texts.Select(block => block.Inlines is { Count: > 0 } ? FlattenInlines(block.Inlines) : block.Text));
        window.Close();
        return (texts.Count, grids, text);
    });

    [Fact]
    public async Task Matches_MarkTheFoundTextInTheRenderedBlocks()
    {
        (string plain, List<string> marked) = await fixture.Run(() =>
        {
            MarkdownView view = new() { Source = "# Title\n\nsome **bold** text and bold again" };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string plain = view.PlainText;
            view.Matches = new TextMatches { Starts = [plain.IndexOf("bold", StringComparison.Ordinal), plain.LastIndexOf("bold", StringComparison.Ordinal)], Length = 4, Current = 1 };
            List<string> marked = view.GetVisualDescendants().OfType<SelectableTextBlock>().SelectMany(block => block.Inlines!.OfType<Span>().SelectMany(span => span.Inlines).Concat(block.Inlines!).OfType<Run>()).Where(run => run.Background is not null && run.Text == "bold").Select(run => run.Text!).ToList();
            window.Close();
            return (plain, marked);
        });

        Assert.Equal("Title\nsome bold text and bold again\n", plain);
        Assert.Equal(2, marked.Count);
    }

    [Fact]
    public async Task Source_RendersEveryKindOfBlock()
    {
        (int texts, int grids, string text) = await RenderMarkdown(markdownSample);

        Assert.True(texts >= 10);
        Assert.True(grids >= 2);
        Assert.Contains("Title", text);
        Assert.Contains("int x = 1;", text);
        Assert.Contains("italic", text);
        Assert.Contains("nested", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public async Task Source_NullOrEmptyRendersNothing()
    {
        Assert.Equal(0, (await RenderMarkdown(null)).Texts);
        Assert.Equal(0, (await RenderMarkdown(string.Empty)).Texts);
    }

    private async Task<(int Runs, int Colored, string Text)> RenderFile(FileContent? file) => await fixture.Run(() =>
    {
        SyntaxView view = new() { File = file };
        Window window = new() { Width = 390, Height = 800, Content = view };
        window.Show();
        SelectableTextBlock? block = view.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
        int runs = block?.Inlines?.Count ?? 0;
        int colored = block?.Inlines?.OfType<Run>().Count(run => run.IsSet(TextElement.ForegroundProperty)) ?? 0;
        string text = block is null ? string.Empty : block.Inlines is { Count: > 0 } ? FlattenInlines(block.Inlines) : block.Text ?? string.Empty;
        window.Close();
        return (runs, colored, text);
    });

    [Fact]
    public async Task SyntaxView_ColorsARecognizedLanguageAndKeepsTheText()
    {
        (int runs, int colored, string text) = await RenderFile(new FileContent { Path = "a.cs", Text = "public int X = 1; // c" });

        Assert.True(runs > colored);
        Assert.True(colored >= 4);
        Assert.Equal("public int X = 1; // c", text);
    }

    [Fact]
    public async Task SyntaxView_ShowsAnUnrecognizedFileAsPlainText()
    {
        (int runs, int colored, string text) = await RenderFile(new FileContent { Path = "a.txt", Text = "public int" });

        Assert.Equal(0, runs);
        Assert.Equal(0, colored);
        Assert.Equal("public int", text);
        Assert.Equal(0, (await RenderFile(null)).Runs);
    }

    [Fact]
    public async Task SyntaxView_ShowsLineNumbersAndMarksMatches()
    {
        (string gutter, int marked) = await fixture.Run(() =>
        {
            SyntaxView view = new() { File = new FileContent { Path = "a.cs", Text = "int a;\nint b;\n" }, Matches = new TextMatches { Starts = [4, 11], Length = 1, Current = 1 } };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string numbers = view.GetVisualDescendants().OfType<TextBlock>().First(block => block is not SelectableTextBlock).Text ?? string.Empty;
            int highlighted = view.GetVisualDescendants().OfType<SelectableTextBlock>().First().Inlines!.OfType<Run>().Count(run => run.Background is not null);
            window.Close();
            return (numbers, highlighted);
        });

        Assert.Equal("1\n2\n3", gutter);
        Assert.Equal(2, marked);
    }

    private bool Shows<TView>(UserControl view, object model)
        where TView : Control
    {
        view.DataContext = model;
        Window window = new() { Width = 390, Height = 800, Content = view };
        window.Show();
        bool found = window.GetVisualDescendants().OfType<TView>().Any() || view is TView;
        window.Close();
        return found;
    }

    [Fact]
    public async Task ConnectView_Builds() => Assert.True(await fixture.Run(() => Shows<ConnectView>(new ConnectView(), new ConnectViewModel(new Mock<IServerConnection.IFactory>().Object, null, "v"))));

    [Fact]
    public async Task NewWorkspaceView_Builds() => Assert.True(await fixture.Run(() => Shows<NewWorkspaceView>(new NewWorkspaceView(), new NewWorkspaceViewModel(connection.Object))));

    [Fact]
    public async Task HomeView_BuildsEmptyAndWithWorkspace()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", GroupId = group.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [group], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }, new ChatMessage { Role = MessageRole.Assistant, Text = "hello" }], Start = 0 });
        connection.Setup(x => x.ListFiles(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false, Size = 1 }] });

        Assert.True(await fixture.Run(() => Shows<HomeView>(new HomeView(), new HomeViewModel(connection.Object))));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.Active!.ShowFilesCommand.Execute(null);
            model.BeginDeleteCommand.Execute(null);
            return Shows<HomeView>(new HomeView(), model);
        }));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.Active!.ToggleSettingsCommand.Execute(null);
            model.IsDrawerOpen = true;
            model.BeginRenameGroupCommand.Execute(group);
            return Shows<HomeView>(new HomeView(), model);
        }));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.BeginMoveWorkspaceCommand.Execute(workspace);
            return Shows<HomeView>(new HomeView(), model);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeView_ShowsEqualTabsAGearAtTheTopRightAndTurnsTheTopBarAccentBlueWhileTheAiWorks(bool working)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0, Working = working });

        (string[] labels, double[] widths, bool accented, bool paintedAccent, bool statusText, bool gearAtTopRight) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            List<Button> tabs = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible)];
            string[] labels = [.. tabs.Select(tab => tab.Content?.ToString() ?? string.Empty)];
            double[] widths = [.. tabs.Select(tab => Math.Round(tab.Bounds.Width))];
            Border topBar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("topbar"));
            bool accented = topBar.Classes.Contains("working");
            bool paintedAccent = topBar.Background is Avalonia.Media.ISolidColorBrush brush && brush.Color == Avalonia.Media.Color.Parse("#007ACC");
            bool statusText = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text is "AI is waiting" or "AI is working...");
            Button gear = topBar.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "\u2699\uFE0E");
            bool gearAtTopRight = gear.IsVisible && Avalonia.VisualExtensions.TranslatePoint(gear, new Avalonia.Point(0, 0), window)!.Value.X > window.Width / 2;
            window.Close();
            return (labels, widths, accented, paintedAccent, statusText, gearAtTopRight);
        });

        Assert.Equal(["Chat", "Files", "Script"], labels);
        Assert.Single(widths.Distinct());
        Assert.True(gearAtTopRight);
        Assert.Equal(working, accented);
        Assert.Equal(working, paintedAccent);
        Assert.False(statusText);
    }

    [Fact]
    public async Task HomeView_ShowsAnEqualHistoryTabOnlyWhenTheWorkspaceIsAGitRepository()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "first", HasChanges = true });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([new GitChange { Path = "a.txt", Status = GitChangeStatus.Modified }]);
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false }] });

        (string[] labels, double[] widths, string[] versionTexts) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.Active!.ShowFilesCommand.Execute(null);
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            List<Button> tabs = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible)];
            string[] versionTexts = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("versionSection")).SelectMany(button => button.GetVisualDescendants().OfType<TextBlock>()).Select(block => block.Text ?? string.Empty)];
            string[] labels = [.. tabs.Select(tab => tab.Content?.ToString() ?? string.Empty)];
            double[] widths = [.. tabs.Select(tab => Math.Round(tab.Bounds.Width))];
            window.Close();
            return (labels, widths, versionTexts);
        });

        Assert.Equal(["Chat", "Files", "Script", "History"], labels);
        Assert.Single(widths.Distinct());
        Assert.Contains("main", versionTexts);
        Assert.Contains("first", versionTexts);
        Assert.Contains("abc1234", versionTexts);
        Assert.Contains("*", versionTexts);
    }

    [Fact]
    public async Task HomeView_DesktopLayoutShowsScriptTabWindowControlsAndTheHistoryGraph()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [new ScriptRun { Path = "a.cs", Name = "a", State = ScriptState.Succeeded, Output = "hello" }] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "merge" });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }] });
        GitCommit Commit(string hash, params string[] parents) => new() { Hash = hash, Subject = "commit " + hash, Author = "Ann", Date = DateTimeOffset.UnixEpoch, Parents = parents };
        connection.Setup(x => x.GetGitHistory(workspace.Id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [Commit("m", "a", "b"), Commit("a", "base"), Commit("b", "base"), Commit("base")] });

        (string[] labels, int controls, int graphs, int[] lanes, string output) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            model.Active!.Scripts.Start().GetAwaiter().GetResult();
            model.Active.Files.Start().GetAwaiter().GetResult();
            model.Active.Git.History.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            string[] labels = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty)];
            int controls = window.GetVisualDescendants().OfType<WindowControlsView>().Count(item => item.IsEffectivelyVisible);
            model.Active.ShowHistoryCommand.Execute(null);
            window.UpdateLayout();
            List<GitGraphView> graphs = [.. window.GetVisualDescendants().OfType<GitGraphView>().Where(item => item.IsEffectivelyVisible)];
            model.Active.ShowScriptCommand.Execute(null);
            model.Active.Scripts.SelectedEntry = model.Active.Scripts.Entries[0];
            window.UpdateLayout();
            string output = string.Concat(window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(item => item.IsEffectivelyVisible).Select(item => item.Text));
            window.Close();
            return (labels, controls, graphs.Count, graphs.Select(item => item.Row!.Lane).ToArray(), output);
        });

        Assert.Equal(["Chat", "Script", "History"], labels);
        Assert.Equal(1, controls);
        Assert.Equal(4, graphs);
        Assert.Equal([0, 0, 1, 0], lanes);
        Assert.Contains("hello", output);
    }

    [Fact]
    public async Task HomeView_GitPromptFocusesItsBoxEscapeCancelsAndEnterSubmits()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "base", HasIdentity = true, HasChanges = true });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Ready);

        (bool focused, bool capitalizes, GitPrompt afterEscape, bool urlCapitalizes, string submitted) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.Active!.Git.Refresh().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            GitViewModel git = model.Active.Git;
            git.CommitCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TextBox box = window.GetVisualDescendants().OfType<TextBox>().First(item => item.Name == "PromptInput");
            bool focused = box.IsFocused;
            bool capitalizes = Avalonia.Input.TextInput.TextInputOptions.GetAutoCapitalization(box);
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            GitPrompt afterEscape = git.Prompt;
            git.RemoteCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool urlCapitalizes = Avalonia.Input.TextInput.TextInputOptions.GetAutoCapitalization(box);
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            git.CommitCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            git.PromptText = "my message";
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Close();
            return (focused, capitalizes, afterEscape, urlCapitalizes, "my message");
        });
        await Task.Delay(100);

        Assert.True(focused);
        Assert.True(capitalizes);
        Assert.Equal(GitPrompt.None, afterEscape);
        Assert.False(urlCapitalizes);
        connection.Verify(x => x.GitCommit(workspace.Id, submitted, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HomeView_MessageBoxShowsStopAndCancelInsteadOfSendWhileWorkRunsAndAsksFirst()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Ready);

        (string[] ready, string[] working, bool[] enabled, bool typing, bool asking, string dismiss, bool askedAndNothingSent) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string[] Shown() => [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Command == model.Active?.Chat.SendCommand ? "send" : button.Command == model.BeginStopCommand ? "stop" : button.Command == model.BeginCancelCommand ? "cancel" : string.Empty).Where(name => name.Length > 0)];
            string[] ready = Shown();
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
            string[] working = Shown();
            bool[] enabled = [model.BeginStopCommand.CanExecute(null), model.BeginCancelCommand.CanExecute(null)];
            model.Active!.Chat.Input = "one more thing";
            bool typing = Shown().Contains("send");
            model.Active.Chat.Input = string.Empty;
            model.BeginStopCommand.Execute(null);
            bool asking = model.IsStoppingWork;
            string dismiss = model.CancelEditText;
            model.CancelEditCommand.Execute(null);
            window.Close();
            return (ready, working, enabled, typing, asking, dismiss, !model.IsEditing);
        });

        Assert.Equal(["send"], ready);
        Assert.Equal(["stop", "cancel"], working);
        Assert.Equal([true, false], enabled);
        Assert.True(typing);
        Assert.True(asking);
        Assert.Equal("Keep working", dismiss);
        Assert.True(askedAndNothingSent);
        connection.Verify(x => x.StopWorkspace(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeView_TabsLayoutHasNoWindowControlsButAScriptTab()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });

        (string[] labels, int controls) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string[] labels = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty)];
            int controls = window.GetVisualDescendants().OfType<WindowControlsView>().Count(item => item.IsEffectivelyVisible);
            window.Close();
            return (labels, controls);
        });

        Assert.Equal(["Chat", "Files", "Script"], labels);
        Assert.Equal(0, controls);
    }

    [Theory]
    [InlineData(false, "1\n2\n3")]
    [InlineData(true, "1\n2\n\n\n3")]
    public async Task SyntaxView_NumbersLogicalLinesAndLeavesWrappedContinuationsBlank(bool wrap, string expected)
    {
        string longLine = string.Join(' ', Enumerable.Repeat("word", 40));

        string numbers = await fixture.Run(() =>
        {
            SyntaxView view = new() { Wrap = wrap, File = new FileContent { Path = "notes.txt", Text = $"short\n{longLine}\nlast" } };
            Window window = new() { Width = 400, Height = 600, Content = new ScrollViewer { Content = view } };
            window.Show();
            window.UpdateLayout();
            string result = view.GetVisualDescendants().OfType<TextBlock>().First(block => block is not SelectableTextBlock).Text ?? string.Empty;
            window.Close();
            return result;
        });

        string[] lines = numbers.Split('\n');
        Assert.Equal(expected.Split('\n').Where(line => line.Length > 0), lines.Where(line => line.Length > 0));

        if (wrap)
        {
            Assert.True(lines.Length > 3);
        }
    }

    [Fact]
    public async Task MainView_ShowsScreenForCurrentViewModel()
    {
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Name).Returns("Auto Remote");
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<ISettingsStore> store = new();
        MainViewModel model = new(info.Object, new Mock<IServerConnection.IFactory>().Object, store.Object);

        Assert.True(await fixture.Run(() =>
        {
            model.Start().GetAwaiter().GetResult();
            return Shows<ConnectView>(new MainView(), model);
        }));
    }
}
