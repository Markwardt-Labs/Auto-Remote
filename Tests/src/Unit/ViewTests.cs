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

    [Theory]
    [InlineData(390)]
    [InlineData(1000)]
    public async Task EnvironmentView_GivesTheContextSizeOfEachLocalModelItsOwnWideFieldSoATypicalSizeFits(int width)
    {
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { LocalModels = new CapabilityNames { Fast = "a", Balanced = "b", Strong = "c", Max = "d" }, LocalContexts = new CapabilityContexts { Fast = 131072, Balanced = 262144, Strong = 32768, Max = 1048576 }, LocalOptimizeContext = true, LocalServerNote = "Auto Remote started Ollama." });

        (double[] widths, string[] texts, bool optimize, bool note) = await fixture.Run(() =>
        {
            EnvironmentViewModel model = new(connection.Object);
            model.Load().GetAwaiter().GetResult();
            Window window = new() { Width = width, Height = 1400, Content = new EnvironmentView { DataContext = model } };
            window.Show();
            Expand(window);
            window.UpdateLayout();
            List<NumericUpDown> fields = [.. window.GetVisualDescendants().OfType<NumericUpDown>().Where(field => field.IsEffectivelyVisible && field.PlaceholderText == "Context size (tokens)")];
            bool optimize = window.GetVisualDescendants().OfType<CheckBox>().Any(box => box.IsEffectivelyVisible && Equals(box.Content, "Optimize context") && box.IsChecked == true);
            bool note = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "Auto Remote started Ollama.");
            double[] widths = [.. fields.Select(field => field.Bounds.Width)];
            string[] texts = [.. fields.Select(field => field.Text ?? string.Empty)];
            window.Close();
            return (widths, texts, optimize, note);
        });

        Assert.Equal(["131072", "262144", "32768", "1048576"], texts);
        Assert.All(widths, fieldWidth => Assert.True(fieldWidth >= 200, $"The context field is {fieldWidth} wide."));
        Assert.True(optimize);
        Assert.True(note);
    }

    [Fact]
    public async Task EnvironmentView_ShowsThePasswordDialogOnlyWhileSwitchingOptimizeContextOnAControllableService()
    {
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { OllamaServiceControl = true, OllamaServiceNeedsPassword = true });

        (bool before, bool opened, bool password, bool title, bool after) = await fixture.Run(() =>
        {
            EnvironmentViewModel model = new(connection.Object);
            model.Load().GetAwaiter().GetResult();
            Window window = new() { Width = 500, Height = 1400, Content = new EnvironmentView { DataContext = model } };
            window.Show();
            Expand(window);
            window.UpdateLayout();
            bool Visible(Func<Avalonia.Visual, bool> match) => window.GetVisualDescendants().Any(visual => visual.IsEffectivelyVisible && match(visual));
            bool before = Visible(visual => visual is TextBlock { Text: "Optimize Ollama context?" });
            model.LocalOptimizeContext = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool opened = Visible(visual => visual is TextBlock { Text: "Optimize Ollama context?" });
            bool password = Visible(visual => visual is TextBox { PasswordChar: '\u2022', PlaceholderText: "Password" });
            bool title = Visible(visual => visual is Button { Content: "Optimize and Restart" });
            model.CancelServiceCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool after = Visible(visual => visual is TextBlock { Text: "Optimize Ollama context?" });
            window.Close();
            return (before, opened, password, title, after);
        });

        Assert.False(before);
        Assert.True(opened);
        Assert.True(password);
        Assert.True(title);
        Assert.False(after);
    }

    [Fact]
    public async Task HomeView_ShowsWorkWaitingForTheLocalAiInTheStatusLineWithAButtonThatCancelsItUntilNothingWaits()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Local };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace], Queued = [workspace.Id] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetQueuedWork(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new QueueStatus { Position = 1, Text = "fix the build", Running = ["web"] });

        (bool shown, bool button, bool marked, bool gone) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Border Banner() => window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "StatusLine");
            bool shown = Banner().IsEffectivelyVisible && Banner().GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Waiting for Local AI, position 1");
            bool button = Banner().GetVisualDescendants().OfType<Button>().Any(candidate => candidate.IsEffectivelyVisible && Equals(candidate.Content, "Cancel Waiting Work"));
            bool marked = model.Entries.Any(entry => entry.IsQueued && entry.Detail.StartsWith("Waiting for Local AI", StringComparison.Ordinal));
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspace.Id });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool gone = !Banner().IsEffectivelyVisible;
            window.Close();
            return (shown, button, marked, gone);
        });

        Assert.True(shown);
        Assert.True(button);
        Assert.True(marked);
        Assert.True(gone);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1000)]
    public async Task SwarmView_ShowsThePeerItsSwitchesAndAnOpenedTaskWithItsActions(int width)
    {
        SwarmTaskInfo task = new() { Id = Guid.NewGuid(), Title = "fix", State = SwarmTaskState.Done, PeerName = "there", Branch = "swarm/x" };
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmStatus { Server = new SwarmServer { Name = "here", Fingerprint = "AAAA-BBBB" }, Peers = [new SwarmPeer { Id = "peer", Name = "there", Fingerprint = "CCCC-DDDD", Mode = SwarmMode.Direct }], Tasks = [task] });
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });
        connection.Setup(x => x.GetSwarmTask(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmTaskDetail { Task = task, Instructions = "do it", Report = "done it" });

        (string[] switches, bool revoke, string[] actions, bool report) = await fixture.Run(() =>
        {
            SwarmViewModel model = new(connection.Object);
            model.Load().GetAwaiter().GetResult();
            Window window = new() { Width = width, Height = 2400, Content = new SwarmView { DataContext = model } };
            window.Show();
            Expand(window);
            Expand(window);
            window.UpdateLayout();
            string[] switches = [.. window.GetVisualDescendants().OfType<ToggleSwitch>().Where(toggle => toggle.IsEffectivelyVisible).Select(toggle => toggle.Content?.ToString() ?? string.Empty)];
            bool revoke = window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && Equals(button.Content, "Revoke Server"));
            model.OpenTaskCommand.Execute(model.Tasks[0]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            string[] actions = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty).Where(content => content.EndsWith("Task", StringComparison.Ordinal) || content.StartsWith("Bring Into", StringComparison.Ordinal))];
            bool report = window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "done it");
            window.Close();
            model.Dispose();
            return (switches, revoke, actions, report);
        });

        Assert.Equal(["Accept delegated work", "Send work to this server", "Accept work from this server", "Sandbox", "Use the web", "Generate images"], switches);
        Assert.True(revoke);
        Assert.Equal(["Bring Into Main", "Discard Task"], actions);
        Assert.True(report);
    }

    [Fact]
    public async Task HomeView_DisablesTheMessageBoxAndTheSettingsOfADelegatedWorkspaceAndSaysWhatStoppingItDoes()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/data/task", Service = AiService.Local };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace], Delegations = [new WorkspaceDelegation { WorkspaceId = workspace.Id, From = "laptop", Title = "t" }] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = true });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Service = AiService.Local });

        (bool box, string? stopTip, bool stopNote, bool lockedNote, int switches, int enabledSwitches) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 1600, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool box = window.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "ChatInput").IsEffectivelyEnabled;
            string? stopTip = window.GetVisualDescendants().OfType<Button>().Select(button => ToolTip.GetTip(button) as string).FirstOrDefault(tip => tip is "Cancel the delegated task" or "Stop the work and keep what it did");
            model.BeginStopCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool stopNote = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "This cancels the task delegated by laptop, which is told.");
            model.CancelEditCommand.Execute(null);
            model.Active!.ToggleSettingsCommand.Execute(null);
            Expand(window);
            window.UpdateLayout();
            bool lockedNote = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "This workspace runs a task delegated by another server, so its settings cannot be changed.");
            List<ToggleSwitch> found = [.. window.GetVisualDescendants().OfType<ToggleSwitch>().Where(item => item.IsEffectivelyVisible)];
            window.Close();
            return (box, stopTip, stopNote, lockedNote, found.Count, found.Count(item => item.IsEffectivelyEnabled));
        });

        Assert.False(box);
        Assert.Equal("Cancel the delegated task", stopTip);
        Assert.True(stopNote);
        Assert.True(lockedNote);
        Assert.Equal((4, 0), (switches, enabledSwitches));
    }

    private void Expand(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        foreach (Expander section in window.GetVisualDescendants().OfType<Expander>())
        {
            section.IsExpanded = true;
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

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

    private readonly string foldable = "# One\n\nfirst body\n\n## Two\n\nsecond body\n\n### Three\n\nthird body\n\n## Four\n\nfourth body\n\n# Five\n\nfifth body";

    private async Task<T> Fold<T>(bool collapsible, Func<MarkdownView, List<Button>, T> act) => await fixture.Run(() =>
    {
        MarkdownView view = new() { Source = foldable, Collapsible = collapsible };
        Window window = new() { Width = 390, Height = 800, Content = view };
        window.Show();
        T result = act(view, [.. view.GetVisualDescendants().OfType<Button>()]);
        window.Close();
        return result;
    });

    private List<string> VisibleText(MarkdownView view) => [.. view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? FlattenInlines(block.Inlines!))];

    [Fact]
    public async Task Collapsible_GivesEveryHeadingAFoldThatHidesItsContentUpToTheNextHeadingOfTheSameOrAHigherLevel()
    {
        (int folds, List<string> before, List<string> afterTwo, List<string> afterOne) = await Fold(true, (view, buttons) =>
        {
            List<string> before = VisibleText(view);
            buttons[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            List<string> afterTwo = VisibleText(view);
            buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return (buttons.Count, before, afterTwo, VisibleText(view));
        });

        Assert.Equal(5, folds);
        Assert.Contains("third body", before);
        Assert.DoesNotContain("second body", afterTwo);
        Assert.DoesNotContain("third body", afterTwo);
        Assert.Contains("fourth body", afterTwo);
        Assert.DoesNotContain("fourth body", afterOne);
        Assert.Contains("fifth body", afterOne);
        Assert.Contains("Five", afterOne);
    }

    [Fact]
    public async Task Collapsible_IsOffByDefaultSoChatMarkdownHasNoFolds()
    {
        int folds = await Fold(false, (_, buttons) => buttons.Count);

        Assert.Equal(0, folds);
    }

    [Fact]
    public async Task AllCollapsed_CollapsesAndExpandsEveryHeadingAndFollowsTheFoldsOneByOne()
    {
        (List<string> collapsed, bool afterOpening, List<string> expanded, bool afterFolding) = await Fold(true, (view, buttons) =>
        {
            view.AllCollapsed = true;
            List<string> collapsed = VisibleText(view);
            buttons[3].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            bool afterOpening = view.AllCollapsed;
            view.AllCollapsed = true;
            view.AllCollapsed = false;
            List<string> expanded = VisibleText(view);
            foreach (Button button in buttons)
            {
                if (view.AllCollapsed)
                {
                    break;
                }

                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }

            return (collapsed, afterOpening, expanded, view.AllCollapsed);
        });

        Assert.DoesNotContain("first body", collapsed);
        Assert.False(afterOpening);
        Assert.Contains("fourth body", expanded);
        Assert.Contains("third body", expanded);
        Assert.True(afterFolding);
    }

    [Fact]
    public async Task Matches_ExpandTheFoldsAroundTheCurrentMatchWhenNavigatingAndKeepTheOthersCollapsedAcrossRenders()
    {
        (List<string> visible, bool all) = await Fold(true, (view, _) =>
        {
            view.AllCollapsed = true;
            int at = view.PlainText.IndexOf("third", StringComparison.Ordinal);
            view.Matches = new TextMatches { Starts = [at], Length = 5, Current = 0, Navigate = true };
            return (VisibleText(view), view.AllCollapsed);
        });

        Assert.Contains("third body", visible);
        Assert.Contains("second body", visible);
        Assert.DoesNotContain("fourth body", visible);
        Assert.DoesNotContain("fifth body", visible);
        Assert.False(all);
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

    [Theory]
    [InlineData("before\n\n```\n```")]
    [InlineData("before\n\n```bash")]
    [InlineData("before\n\n~~~\n~~~")]
    [InlineData("before\n\n$$")]
    public async Task Source_ShowsTheRestOfADocumentWithAnEmptyCodeBlock(string source)
    {
        (int _, int _, string text) = await RenderMarkdown(source);

        Assert.Contains("before", text);
    }

    [Fact]
    public async Task Source_ShowsEntitiesAsTheirCharactersAndDollarSignsAsWritten()
    {
        (int _, int _, string text) = await RenderMarkdown("a &lt; b &amp;&amp; 5&nbsp;MB, use $PATH$ or pay $5$ now");

        Assert.Contains("a < b && 5\u00a0MB, use $PATH$ or pay $5$ now", text.Replace("|", string.Empty));
    }

    [Fact]
    public async Task Source_ShowsTextNestedTooDeeplyToLayOutAsItIs()
    {
        string source = new string('>', 300) + " deep";

        (int _, int _, string text) = await RenderMarkdown(source);

        Assert.Contains("deep", text);
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
    public async Task ConnectView_ShowsAButtonForTheLocalServerAndNoneWithoutOne()
    {
        Mock<ILocalServers> locals = new();
        locals.Setup(x => x.Find(It.IsAny<CancellationToken>())).ReturnsAsync([new LocalServer { Port = 5000, Version = "1.0.0" }]);
        ConnectViewModel model = new(new Mock<IServerConnection.IFactory>().Object, null, "v", locals.Object);
        Task watching = model.Watch();

        for (int attempt = 0; attempt < 250 && !model.HasLocalServers; attempt++)
        {
            await Task.Delay(20);
        }

        string[] withLocal = await fixture.Run(() => ConnectButtons(model));
        string[] without = await fixture.Run(() => ConnectButtons(new ConnectViewModel(new Mock<IServerConnection.IFactory>().Object, null, "v")));
        model.Stop();
        await watching;

        Assert.Equal(["Connect to Local Server", "Connect"], withLocal);
        Assert.Equal(["Connect"], without);
    }

    private string[] ConnectButtons(ConnectViewModel model)
    {
        ConnectView view = new() { DataContext = model };
        Window window = new() { Width = 390, Height = 800, Content = view };
        window.Show();
        string[] buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible).Select(button => $"{button.Content}")];
        window.Close();
        return buttons;
    }

    [Fact]
    public async Task NewWorkspaceView_Builds() => Assert.True(await fixture.Run(() => Shows<NewWorkspaceView>(new NewWorkspaceView(), new NewWorkspaceViewModel(connection.Object))));

    [Fact]
    public async Task HomeView_BuildsEmptyAndWithWorkspace()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", GroupId = group.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [group], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }, new ChatMessage { Role = MessageRole.Assistant, Text = "hello" }] });
        connection.Setup(x => x.ListFiles(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false, Size = 1 }] });

        Assert.True(await fixture.Run(() => Shows<HomeView>(new HomeView(), new HomeViewModel(connection.Object))));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            model.Active!.ShowFilesCommand.Execute(null);
            model.BeginDeleteCommand.Execute(null);
            return Shows<HomeView>(new HomeView(), model);
        }));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            model.Active!.ToggleSettingsCommand.Execute(null);
            model.IsDrawerOpen = true;
            model.BeginRenameGroupCommand.Execute(group);
            return Shows<HomeView>(new HomeView(), model);
        }));
        Assert.True(await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(workspace.Id).GetAwaiter().GetResult();
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working });

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

        Assert.Equal(["Chat", "Files", "Script", "Activity"], labels);
        Assert.Single(widths.Distinct());
        Assert.True(gearAtTopRight);
        Assert.Equal(working, accented);
        Assert.Equal(working, paintedAccent);
        Assert.False(statusText);
    }

    [Fact]
    public async Task HomeView_SettingsShowTheLockedProviderAndOneControlForEachSetting()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Codex };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Service = AiService.Codex, Capability = AiCapability.Strong, Effort = AiEffort.Low, IsSandboxed = true, Instructions = "be brief", Descriptors = new Dictionary<string, string> { ["Model"] = "GPT 5.6" } });

        (int choiceBoxes, int toggles, bool delegationShown, int numbers, bool providerShown, bool instructionsShown, bool detailShown, string capability) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            model.Active!.ToggleSettingsCommand.Execute(null);
            Expand(window);

            for (int attempt = 0; attempt < 100 && model.Active.Settings.Instructions.Length == 0; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            int choiceBoxes = window.GetVisualDescendants().OfType<ComboBox>().Count(box => box.IsEffectivelyVisible);
            int toggles = window.GetVisualDescendants().OfType<ToggleSwitch>().Count(item => item.IsEffectivelyVisible);
            int numbers = window.GetVisualDescendants().OfType<NumericUpDown>().Count(item => item.IsEffectivelyVisible);
            TextBlock[] texts = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible)];
            bool providerShown = texts.Any(block => block.Text == "OpenAI Codex");
            bool instructionsShown = window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.Text == "be brief");
            bool detailShown = texts.Any(block => block.Text == "GPT 5.6");
            string capability = model.Active.Settings.SelectedCapability.Name;
            bool delegationShown = window.GetVisualDescendants().OfType<ToggleSwitch>().Any(item => item.IsEffectivelyVisible && Equals(item.Content, "Let the AI delegate work"));
            window.Close();
            return (choiceBoxes, toggles, delegationShown, numbers, providerShown, instructionsShown, detailShown, capability);
        });

        Assert.Equal(2, choiceBoxes);
        Assert.Equal(4, toggles);
        Assert.True(delegationShown);
        Assert.Equal(1, numbers);
        Assert.True(providerShown);
        Assert.True(instructionsShown);
        Assert.True(detailShown);
        Assert.Equal("Strong", capability);
    }

    private (bool Header, string[] Messages, bool NoneShown) ErrorPanel(WorkspaceInfo workspace, int expected)
    {
        HomeViewModel model = new(connection.Object);
        model.Start().GetAwaiter().GetResult();
        HomeView view = new() { DataContext = model };
        Window window = new() { Width = 900, Height = 900, Content = view };
        window.Show();
        model.Active!.ToggleSettingsCommand.Execute(null);

        for (int attempt = 0; attempt < 100 && model.Active.Errors.Entries.Count != expected; attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        Expand(window);
        TextBlock[] texts = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible)];
        bool header = window.GetVisualDescendants().OfType<Expander>().Any(section => section.IsEffectivelyVisible && $"{section.Header}" == "Errors");
        string[] messages = [.. texts.Where(block => block is SelectableTextBlock).Select(block => block.Text ?? string.Empty)];
        bool none = texts.Any(block => block.Text == "No errors in the last 30 days.");
        window.Close();
        return (header, messages, none);
    }

    [Fact]
    public async Task HomeView_SettingsListTheErrorsOfTheWorkspaceWithTheirTimesOrSayThereAreNone()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.GetErrors(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([new ErrorEntry { Time = DateTimeOffset.UtcNow, Message = "Codex could not be started" }, new ErrorEntry { Time = DateTimeOffset.UtcNow - TimeSpan.FromDays(3), Message = "Rate limit reached" }]);

        (bool header, string[] messages, bool none) = await fixture.Run(() => ErrorPanel(workspace, 2));

        Assert.True(header);
        Assert.Equal(["Codex could not be started", "Rate limit reached"], messages);
        Assert.False(none);

        connection.Setup(x => x.GetErrors(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        (bool emptyHeader, string[] noMessages, bool noneShown) = await fixture.Run(() => ErrorPanel(workspace, 0));

        Assert.True(emptyHeader);
        Assert.Empty(noMessages);
        Assert.True(noneShown);
    }

    [Fact]
    public async Task HomeView_ChatHasAToggleForTheWorkOfTheAiNextToAutoScrollAndShowsThatWorkDimmed()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", ShowWork = true };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory
        {
            ShowWork = true,
            Messages = [new ChatMessage { Role = MessageRole.User, Text = "list files" }, new ChatMessage { Role = MessageRole.Assistant, Text = "shell: ls -la", IsWork = true, Time = DateTimeOffset.UtcNow }, new ChatMessage { Role = MessageRole.Assistant, Text = "Found three files.", Time = DateTimeOffset.UtcNow }]
        });

        (bool toggled, bool leftOfAutoScroll, string[] dimmed, string[] normal, bool turnedOff) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 900, Content = view };
            window.Show();

            for (int attempt = 0; attempt < 100 && model.Active!.Chat.Messages.Count < 3; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Controls.Primitives.ToggleButton[] buttons = [.. window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(button => button.IsEffectivelyVisible)];
            Avalonia.Controls.Primitives.ToggleButton work = buttons.Single(button => $"{ToolTip.GetTip(button)}".StartsWith("Show the work of the AI", StringComparison.Ordinal));
            Avalonia.Controls.Primitives.ToggleButton auto = buttons.First(button => $"{ToolTip.GetTip(button)}" == "Auto scroll to new messages");
            bool checkedNow = work.IsChecked == true;
            ScrollSafeTextBlock[] texts = [.. window.GetVisualDescendants().OfType<ScrollSafeTextBlock>().Where(block => block.IsEffectivelyVisible)];
            string[] dim = [.. texts.Where(block => block.Foreground is Avalonia.Media.ISolidColorBrush { Color.R: 0x96 }).Select(block => block.Text ?? string.Empty)];
            string[] other = [.. texts.Where(block => block.Foreground is not Avalonia.Media.ISolidColorBrush { Color.R: 0x96 } && !string.IsNullOrEmpty(block.Text)).Select(block => block.Text!)];
            bool left = ReferenceEquals(work.Parent, auto.Parent) && work.Bounds.X < auto.Bounds.X;
            model.Active!.Chat.ShowWork = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool off = work.IsChecked != true;
            window.Close();
            return (checkedNow, left, dim, other, off);
        });

        Assert.True(toggled);
        Assert.True(leftOfAutoScroll);
        Assert.Equal(["shell: ls -la"], dimmed);
        Assert.Equal(["list files"], normal);
        Assert.True(turnedOff);
    }

    [Fact]
    public async Task HomeView_SendOptionsRepeatChoosesWhatHappensWhenEachRunIsDoneStartingAsKeepChangesWheneverItOpens()
    {
        Repository();

        (string[] first, string[] chosen, string[] again, int switches) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, 1200);
            screen.Model.Active!.Chat.Input = "go";
            SendOptionsViewModel options = screen.Model.Active.SendOptions;

            void OpenPanel()
            {
                screen.Model.Active!.OpenSendOptionsCommand.Execute(null);
                screen.Settle();
                options.IsRepeat = true;
                screen.Settle();
            }

            string[] Done() => [.. screen.Named<Grid>("SendOptionsPanel").GetVisualDescendants().OfType<RadioButton>().Where(radio => radio.IsEffectivelyVisible && radio.GroupName == "SendDone").Select(radio => $"{radio.Content}{(radio.IsChecked == true ? "*" : string.Empty)}")];

            OpenPanel();
            string[] first = Done();
            screen.Named<Grid>("SendOptionsPanel").GetVisualDescendants().OfType<RadioButton>().Single(radio => Equals(radio.Content, "Commit")).IsChecked = true;
            screen.Settle();
            string[] chosen = Done();
            int switches = screen.Named<Grid>("SendOptionsPanel").GetVisualDescendants().OfType<ToggleSwitch>().Count(item => item.IsEffectivelyVisible);
            options.CancelCommand.Execute(null);
            OpenPanel();
            return (first, chosen, Done(), switches);
        });

        Assert.Equal(["Commit", "Keep Changes*"], first);
        Assert.Equal(["Commit*", "Keep Changes"], chosen);
        Assert.Equal(first, again);
        Assert.Equal(0, switches);
    }

    [Fact]
    public async Task HomeView_ChatErrorShowsWhenItHappenedAboveItsTextAndGoesWithTheNextInputOfAnyClient()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        DateTimeOffset happened = DateTimeOffset.UtcNow.AddMinutes(-3);

        (string[] shown, string[] after) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 900, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspace.Id, Error = "usage limit reached", Time = happened });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            string[] Texts() => [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.Text is { Length: > 0 }).Select(block => block.Text!)];
            string[] with = Texts();
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Message = new ChatMessage { Role = MessageRole.User, Text = "again", Time = DateTimeOffset.UtcNow } });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string[] without = Texts();
            window.Close();
            return (with, without);
        });

        int error = Array.IndexOf(shown, "usage limit reached");
        Assert.True(error > 0);
        Assert.Equal(happened.ToStamp(DateTimeOffset.UtcNow), shown[error - 1]);
        Assert.DoesNotContain("usage limit reached", after);
        Assert.DoesNotContain(happened.ToStamp(DateTimeOffset.UtcNow), after.Where(text => text != "again"));
    }

    [Fact]
    public async Task HomeView_ShowsTheUpdateButtonNextToTheWorkspacesButtonOnlyWhenAnUpdateIsAvailableAndConfirmsBeforeUpdating()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });
        connection.SetupGet(x => x.ServerVersion).Returns("1.0.0");
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<IReleaseFeed> feed = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseInfo { Version = new AppVersion { Major = 1, Minor = 1, Patch = 0 }, Assets = [] });

        (bool hiddenWithout, bool hiddenBefore, bool shown, bool besideMenu, bool dialogBefore, bool dialogAfter, string[] texts, bool drawerButton) = await fixture.Run(() =>
        {
            bool drawerButton;
            HomeViewModel plain = new(connection.Object);
            HomeView plainView = new() { DataContext = plain };
            Window plainWindow = new() { Width = 900, Height = 900, Content = plainView };
            plainWindow.Show();
            bool hiddenWithout = !plainWindow.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString() == "Update available");
            plainWindow.Close();

            UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
            HomeViewModel model = new(connection.Object, ClientLayout.Panels, null, null, null, null, null, null, null, update);
            model.IsDrawerOpen = false;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            bool hiddenBefore = !window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString() == "Update available");
            update.Check().GetAwaiter().GetResult();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Button menu = window.GetVisualDescendants().OfType<Button>().First(button => button.Content?.ToString() == "\u2630");
            Button updating = window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString() == "Update available");
            bool shown = updating.IsEffectivelyVisible && updating.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Stroke is Avalonia.Media.ISolidColorBrush stroke && stroke.Color == Avalonia.Media.Color.Parse("#CCCCCC");
            Avalonia.Point menuPoint = Avalonia.VisualExtensions.TranslatePoint(menu, new Avalonia.Point(0, 0), window)!.Value;
            Avalonia.Point updatePoint = Avalonia.VisualExtensions.TranslatePoint(updating, new Avalonia.Point(0, 0), window)!.Value;
            bool besideMenu = updatePoint.X > menuPoint.X && updatePoint.X - menuPoint.X < 120 && Math.Abs(updatePoint.Y - menuPoint.Y) < 20;
            bool dialogBefore = window.GetVisualDescendants().OfType<UpdateDialogView>().Any(dialog => dialog.IsEffectivelyVisible);
            model.IsDrawerOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            drawerButton = window.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString() == "Update available") == 1;
            model.IsDrawerOpen = false;
            updating.Command!.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            UpdateDialogView dialog = window.GetVisualDescendants().OfType<UpdateDialogView>().Single();
            bool dialogAfter = dialog.IsEffectivelyVisible;
            string[] texts = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? string.Empty), .. dialog.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty)];
            window.Close();
            return (hiddenWithout, hiddenBefore, shown, besideMenu, dialogBefore, dialogAfter, texts, drawerButton);
        });

        Assert.True(hiddenWithout);
        Assert.True(hiddenBefore);
        Assert.True(shown);
        Assert.True(besideMenu);
        Assert.True(drawerButton);
        Assert.False(dialogBefore);
        Assert.True(dialogAfter);
        Assert.Contains("Update Auto Remote?", texts);
        Assert.Contains("Update", texts);
        Assert.Contains("Cancel", texts);
        connection.Verify(x => x.UpdateServer(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdatingView_ShowsTheStatusTheProgressAndCloseOnlyWhenTheScreenMayBeLeft()
    {
        connection.SetupGet(x => x.ServerVersion).Returns("1.0.0");
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<IReleaseFeed> feed = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseInfo { Version = new AppVersion { Major = 1, Minor = 1, Patch = 0 }, Assets = [] });
        TaskCompletionSource gate = new();
        connection.Setup(x => x.UpdateServer(It.IsAny<CancellationToken>())).Returns(gate.Task);

        (string[] running, bool closeWhileRunning, bool closeWhenWaiting) = await fixture.Run(() =>
        {
            UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
            update.Check().GetAwaiter().GetResult();
            update.Begin();
            update.ConfirmCommand.Execute(null);
            UpdatingView view = new() { DataContext = update };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string[] running = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? string.Empty)];
            bool closeWhileRunning = window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Content?.ToString() == "Close");
            gate.SetResult();
            for (int attempt = 0; attempt < 100 && update.Phase != UpdatePhase.Waiting; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool closeWhenWaiting = window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Content?.ToString() == "Close");
            window.Close();
            return (running, closeWhileRunning, closeWhenWaiting);
        });

        Assert.Contains("Updating the server", running);
        Assert.False(closeWhileRunning);
        Assert.True(closeWhenWaiting);
    }

    [Fact]
    public async Task VersionMismatchView_OffersUpdateWhenOneExistsAndAlwaysChangeServer()
    {
        connection.SetupGet(x => x.ServerVersion).Returns("1.2.0");
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<IReleaseFeed> feed = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync((ReleaseInfo?)null);

        (string[] before, string[] after) = await fixture.Run(() =>
        {
            UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
            VersionMismatchViewModel model = new(info.Object, connection.Object, update);
            VersionMismatchView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            string[] Buttons() => [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty)];
            update.Check().GetAwaiter().GetResult();
            string[] before = Buttons();
            feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseInfo { Version = new AppVersion { Major = 1, Minor = 3, Patch = 0 }, Assets = [] });
            update.Check().GetAwaiter().GetResult();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string[] after = Buttons();
            window.Close();
            return (before, after);
        });

        Assert.Equal(["Change Server"], before);
        Assert.Equal(["Update", "Change Server"], after);
    }

    [Fact]
    public async Task HomeView_SidebarHasAGearThatOpensTheEnvironmentSettings()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });

        (bool found, bool raised) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            bool raised = false;
            model.EnvironmentRequested += () => raised = true;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            Button gear = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "\u2699\uFE0E" && ToolTip.GetTip(button)?.ToString() == "AI environment settings");
            gear.Command!.Execute(null);
            window.Close();
            return (gear is not null, raised);
        });

        Assert.True(found);
        Assert.True(raised);
    }

    [Fact]
    public async Task HomeView_SettingsHaveAMoveWorkspaceToFolderButtonThatAsksToMoveTheSelectedWorkspace()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());

        (bool visible, WorkspaceInfo? requested) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            WorkspaceInfo? requested = null;
            model.MoveWorkspaceRequested += asked => requested = asked;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            model.Active!.ToggleSettingsCommand.Execute(null);
            Expand(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Button move = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "Move Workspace to Folder");
            bool visible = move.IsEffectivelyVisible;
            move.Command!.Execute(null);
            window.Close();
            return (visible, requested);
        });

        Assert.True(visible);
        Assert.Equal(workspace, requested);
    }

    [Fact]
    public async Task MoveWorkspaceView_ShowsWhatMovesAndABrowserForTheTarget()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListFolders("/work", It.IsAny<CancellationToken>())).ReturnsAsync(new FolderListing { Path = "/work", Parent = "/", Folders = [new FolderEntry { Name = "other", Path = "/work/other" }] });

        (bool described, bool button) = await fixture.Run(() =>
        {
            MoveWorkspaceViewModel model = new(connection.Object, workspace);
            model.Open().GetAwaiter().GetResult();
            MoveWorkspaceView view = new() { DataContext = model };
            Window window = new() { Width = 600, Height = 900, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool described = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("'app'") == true);
            bool button = window.GetVisualDescendants().OfType<Button>().Any(item => item.Content?.ToString() == "Use This Folder");
            window.Close();
            return (described, button);
        });

        Assert.True(described);
        Assert.True(button);
    }

    [Fact]
    public async Task NewAndCloneWorkspaceViews_OfferTheProvidersAndAKeyFieldForTheOnesThatTakeOne()
    {
        (int providers, int keyFieldsClaude, int keyFieldsLocal) = await fixture.Run(() =>
        {
            NewWorkspaceViewModel model = new(connection.Object);
            NewWorkspaceView view = new() { DataContext = model };
            Window window = new() { Width = 600, Height = 900, Content = view };
            window.Show();
            int providers = window.GetVisualDescendants().OfType<ComboBox>().First().ItemCount;
            TextBox[] Keys() => [.. window.GetVisualDescendants().OfType<TextBox>().Where(box => box.PasswordChar != default && box.IsEffectivelyVisible)];
            int claude = Keys().Length;
            model.Provider.SelectedService = model.Provider.Services.Single(choice => choice.Service == AiService.Local);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            int local = Keys().Length;
            window.Close();
            return (providers, claude, local);
        });
        bool cloneBuilds = await fixture.Run(() => Shows<CloneWorkspaceView>(new CloneWorkspaceView(), new CloneWorkspaceViewModel(connection.Object)));

        Assert.Equal(3, providers);
        Assert.Equal((1, 0), (keyFieldsClaude, keyFieldsLocal));
        Assert.True(cloneBuilds);
    }

    [Fact]
    public async Task EnvironmentView_ShowsTheProviderFieldsAndTheImageFieldsOfTheChosenService()
    {
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { HasClaudeApiKey = true, ImageGeneration = ImageGenerationKind.None });

        (int refreshButtons, int refreshWithLocal, int rowBoxes, int clearBoxes, int switchesAndDefaults, bool defaultsShown) = await fixture.Run(() =>
        {
            EnvironmentViewModel model = new(connection.Object);
            model.Load().GetAwaiter().GetResult();
            EnvironmentView view = new() { DataContext = model };
            Window window = new() { Width = 700, Height = 1400, Content = view };
            window.Show();
            Expand(window);
            int Refresh() => window.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible && button.Content?.ToString() is "Refresh Models" or "Refresh Checkpoints");
            int refreshButtons = Refresh();
            model.SelectedImage = model.ImageKinds.Single(choice => choice.Value == ImageGenerationKind.Local);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            int refreshWithLocal = Refresh();
            int rowBoxes = window.GetVisualDescendants().OfType<ComboBox>().Count(box => box.IsEditable && box.IsEffectivelyVisible);
            int clearBoxes = window.GetVisualDescendants().OfType<CheckBox>().Count(box => box.IsEffectivelyVisible && Equals(box.Content, "A key is set, remove it"));
            int switchesAndDefaults = window.GetVisualDescendants().OfType<ToggleSwitch>().Count(item => item.IsEffectivelyVisible);
            bool defaultsShown = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "New workspaces") && window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.PlaceholderText == "Standing instructions for the AI...");
            window.Close();
            return (refreshButtons, refreshWithLocal, rowBoxes, clearBoxes, switchesAndDefaults, defaultsShown);
        });

        Assert.Equal(1, refreshButtons);
        Assert.Equal(2, refreshWithLocal);
        Assert.Equal(5, switchesAndDefaults);
        Assert.True(defaultsShown);
        Assert.Equal(8, rowBoxes);
        Assert.Equal(1, clearBoxes);
    }

    [Fact]
    public async Task HomeView_BuildsOnlyTheViewOfEachMessageKindLoadsOlderChunksOnlyWhenTheUserScrollsUpAndDropsThemWhenTheEndIsReachedAgain()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", ShowWork = true };
        string tall = string.Join("\n\n", Enumerable.Range(0, 24).Select(line => $"Paragraph {line} of a long answer that wraps over a few lines when the chat is narrow enough."));
        List<ChatMessage> Chunk(int begin, int end) => [.. Enumerable.Range(begin, end - begin).Select(number => number % 3 == 0 ? new ChatMessage { Role = MessageRole.User, Text = $"u{number}" } : number % 3 == 1 ? new ChatMessage { Role = MessageRole.Assistant, Text = $"w{number}", IsWork = true } : new ChatMessage { Role = MessageRole.Assistant, Text = $"a{number}\n\n{tall}" })];
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { ShowWork = true, Messages = Chunk(80, 100), Older = "80" });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), "80", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { ShowWork = true, Messages = Chunk(60, 80), Older = "60" });

        int kept = 0;
        int weight = 0;

        foreach (ChatMessage message in Chunk(60, 100).AsEnumerable().Reverse())
        {
            if (kept > 0 && weight + message.Text.Length > 20_000)
            {
                break;
            }

            weight += message.Text.Length;
            kept++;
        }

        (int markdown, int answers, int loadedBefore, int afterScrollUp, int afterEnd) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 700, Content = view };
            window.Show();

            void Settle(Func<bool> done)
            {
                for (int attempt = 0; attempt < 200 && !done(); attempt++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(10);
                }

                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            ChatViewModel chat = model.Active!.Chat;
            Settle(() => chat.Messages.Count == 20);
            ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "MessageScroller");
            Settle(() => scroller.Extent.Height > scroller.Viewport.Height * 2 && scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 1);
            int markdown = window.GetVisualDescendants().OfType<MarkdownView>().Count();
            int answers = chat.Messages.Count(message => message is { Role: MessageRole.Assistant, IsWork: false });
            int loadedBefore = chat.Messages.Count;

            scroller.Offset = new Avalonia.Vector(0, 0);
            Settle(() => chat.Messages.Count == 40);
            int afterScrollUp = chat.Messages.Count;

            scroller.ScrollToEnd();
            Settle(() => chat.Messages.Count == kept);
            int afterEnd = chat.Messages.Count;
            window.Close();
            return (markdown, answers, loadedBefore, afterScrollUp, afterEnd);
        });

        Assert.Equal(answers, markdown);
        Assert.InRange(kept, 21, 39);
        Assert.Equal((20, 40, kept), (loadedBefore, afterScrollUp, afterEnd));
        connection.Verify(x => x.GetHistory(It.IsAny<Guid>(), "80", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HomeView_DraftsIsTheFirstButtonBesideTheMessageBoxAndHasABlueBorderOnlyWhileThereAreDrafts()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        SavedInput saved = new() { Id = Guid.NewGuid(), Text = "later", Time = DateTimeOffset.UtcNow };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        (bool first, bool without, bool with, bool again) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 800, Content = view };
            window.Show();

            for (int attempt = 0; attempt < 100 && model.Active is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TouchButton load = window.GetVisualDescendants().OfType<TouchButton>().Single(button => button.Name == "DraftsButton" && $"{ToolTip.GetTip(button)}" == "Drafts");
            bool first = ((WrapPanel)load.Parent!).Children[0] == load;
            bool without = load.Classes.Contains("saved");
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspace.Id, SavedInputs = [saved] });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool with = load.Classes.Contains("saved");
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspace.Id, SavedInputs = [] });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool again = load.Classes.Contains("saved");
            window.Close();
            return (first, without, with, again);
        });

        Assert.True(first);
        Assert.False(without);
        Assert.True(with);
        Assert.False(again);
    }

    [Fact]
    public async Task HomeView_ShowsAnAutomaticUsageLimitPauseInTheStatusLineAndAnIconInTheHeaderUntilItEnds()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        LimitPause pause = new() { Window = "5h", PercentUsed = 96, MaxPercent = 95, PausedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(15) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        (bool bannerBefore, bool iconBefore, bool bannerShown, string text, bool iconShown, string tip, bool bannerAfter, bool iconAfter) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 800, Content = view };
            window.Show();

            for (int attempt = 0; attempt < 100 && model.Active is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Border banner = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "StatusLine");
            TouchButton icon = window.GetVisualDescendants().OfType<TouchButton>().Single(button => button.Name == "LimitPauseIndicatorButton");
            bool bannerBefore = banner.IsEffectivelyVisible;
            bool iconBefore = icon.IsEffectivelyVisible;
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspace.Id, LimitPause = pause });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool bannerShown = banner.IsEffectivelyVisible;
            string text = banner.GetVisualDescendants().OfType<TextBlock>().First().Text ?? string.Empty;
            bool iconShown = icon.IsEffectivelyVisible;
            string tip = $"{ToolTip.GetTip(icon)}";
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspace.Id, LimitPause = null });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool bannerAfter = banner.IsEffectivelyVisible;
            bool iconAfter = icon.IsEffectivelyVisible;
            window.Close();
            return (bannerBefore, iconBefore, bannerShown, text, iconShown, tip, bannerAfter, iconAfter);
        });

        Assert.False(bannerBefore);
        Assert.False(iconBefore);
        Assert.True(bannerShown);
        Assert.Equal("Paused at the usage limit", text);
        Assert.True(iconShown);
        Assert.Contains("Paused automatically", tip);
        Assert.Contains("5h usage window is at 96%", tip);
        Assert.False(bannerAfter);
        Assert.False(iconAfter);
    }

    [Fact]
    public async Task HomeView_AsksBeforeStartingWorkOverTheLimitAndHasAControlThatCancelsTheAutomaticResume()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        LimitPause pause = new() { Window = "5h", PercentUsed = 96, MaxPercent = 95, PausedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(15) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], LimitPause = pause });
        connection.Setup(x => x.Prompt(workspace.Id, "go on", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("over", pause));
        connection.Setup(x => x.PromptOverLimit(workspace.Id, "go on", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connection.Setup(x => x.CancelLimitPause(workspace.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        (bool hiddenBefore, string[] buttons, bool shown, string text, bool hiddenAfter, int sentAnyway, int cancelled) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 800, Content = view };
            window.Show();

            void Settle(Func<bool> done)
            {
                for (int attempt = 0; attempt < 200 && !done(); attempt++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }

                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Settle(() => model.Active?.Chat.HasLimitPause == true);
            Border overlay = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "OverLimitOverlay");
            Border banner = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "StatusLine");
            bool hiddenBefore = !overlay.IsEffectivelyVisible;
            string[] buttons = [.. banner.GetVisualDescendants().OfType<TouchButton>().Where(button => button.IsEffectivelyVisible && button.Content is string).Select(button => $"{button.Content}")];

            model.Active!.Chat.Input = "go on";
            model.Active.Chat.SendCommand.Execute(null);
            Settle(() => model.Active.Chat.IsAskingOverLimit);
            bool shown = overlay.IsEffectivelyVisible;
            string text = string.Join(" | ", overlay.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));

            overlay.GetVisualDescendants().OfType<TouchButton>().Single(button => $"{button.Content}" == "Start Anyway").Command!.Execute(null);
            Settle(() => !model.Active.Chat.IsAskingOverLimit && model.Active.Chat.Input.Length == 0);
            bool hiddenAfter = !overlay.IsEffectivelyVisible;
            banner.GetVisualDescendants().OfType<TouchButton>().Single(button => button.Name == "CancelAutoResumeButton").Command!.Execute(null);
            Settle(() => model.Active.Chat.Notice is not null);
            window.Close();
            return (hiddenBefore, buttons, shown, text, hiddenAfter, connection.Invocations.Count(call => call.Method.Name == nameof(IServerConnection.PromptOverLimit)), connection.Invocations.Count(call => call.Method.Name == nameof(IServerConnection.CancelLimitPause)));
        });

        Assert.True(hiddenBefore);
        Assert.Equal(["Cancel Auto Resume"], buttons);
        Assert.True(shown);
        Assert.Contains("Over the usage limit", text);
        Assert.Contains("5h usage window is at 96%", text);
        Assert.True(hiddenAfter);
        Assert.Equal((1, 1), (sentAnyway, cancelled));
    }

    public enum DuringTheLoad
    {
        Nothing,
        TheUserScrollsOn,
        AMessageArrivesAtTheBottom,
        TheTextOfTheOldMessagesReflows,
    }

    [Theory]
    [InlineData(DuringTheLoad.Nothing, false)]
    [InlineData(DuringTheLoad.TheUserScrollsOn, false)]
    [InlineData(DuringTheLoad.AMessageArrivesAtTheBottom, false)]
    [InlineData(DuringTheLoad.TheTextOfTheOldMessagesReflows, false)]
    [InlineData(DuringTheLoad.Nothing, true)]
    public async Task HomeView_KeepsWhatIsOnScreenWhereItIsWhileOlderChunksLoadWhateverElseHappensMeanwhile(DuringTheLoad during, bool autoScroll)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        string tall = string.Join("\n\n", Enumerable.Range(0, 8).Select(line => $"Paragraph {line} of a long answer that wraps over a few lines when the chat is narrow enough."));
        List<ChatMessage> Chunk(int begin, int end) => [.. Enumerable.Range(begin, end - begin).Select(number => number % 2 == 0 ? new ChatMessage { Role = MessageRole.Assistant, Text = $"a{number}\n\n{tall}" } : new ChatMessage { Role = MessageRole.User, Text = $"u{number}" })];
        TaskCompletionSource<ChatHistory> pending = new();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Chunk(80, 100), Older = "80" });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), "80", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        double moved = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 700, Content = view };
            window.Show();

            void Settle(Func<bool> done)
            {
                for (int attempt = 0; attempt < 200 && !done(); attempt++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(10);
                }

                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            ChatViewModel chat = model.Active!.Chat;
            Settle(() => chat.Messages.Count == 20);
            chat.AutoScroll = autoScroll;
            ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "MessageScroller");
            Settle(() => scroller.Extent.Height > scroller.Viewport.Height * 2 && scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 1);
            Control OnScreen(string start) => window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage message && message.Text.StartsWith(start, StringComparison.Ordinal));
            double Y(string start) => Avalonia.VisualExtensions.TranslatePoint(OnScreen(start), new Avalonia.Point(0, 0), scroller)!.Value.Y;

            scroller.Offset = new Avalonia.Vector(0, 300);
            Settle(() => false || scroller.Offset.Y == 300);
            scroller.Offset = new Avalonia.Vector(0, 0);
            Settle(() => false || scroller.Offset.Y == 0);
            Settle(() => chat.HasMore);
            double before = Y("a80");

            if (during == DuringTheLoad.TheUserScrollsOn)
            {
                scroller.Offset = new Avalonia.Vector(0, 25);
                Settle(() => scroller.Offset.Y == 25);
                before = Y("a80");
            }
            else if (during == DuringTheLoad.AMessageArrivesAtTheBottom)
            {
                connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Message = new ChatMessage { Role = MessageRole.Assistant, Text = $"live\n\n{tall}" } });
                Settle(() => false || chat.Messages.Count == 21);
                before = Y("a80");
            }

            pending.SetResult(new ChatHistory { Messages = Chunk(60, 80), Older = "60" });
            Settle(() => chat.Messages.Count >= 40 || (autoScroll && chat.Messages.Count >= 20 && chat.Messages[0].Text.StartsWith("a60", StringComparison.Ordinal)));

            for (int round = 0; round < 5; round++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Thread.Sleep(20);
            }

            if (during == DuringTheLoad.TheTextOfTheOldMessagesReflows)
            {
                before = Y("a80");
                window.Width = 1000;
                Settle(() => false || window.Width == 1000);
            }

            double after = Y("a80");
            window.Close();
            return after - before;
        });

        Assert.InRange(moved, -1.5, 1.5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HomeView_KeepsTheMessageOnScreenInPlaceWhenTheWorkIsTurnedOnOrOffBetweenTheMessages(bool turningOn)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", ShowWork = !turningOn };
        string tall = string.Join("\n\n", Enumerable.Range(0, 5).Select(line => $"Paragraph {line} of a long answer that wraps over a few lines when the chat is narrow enough."));
        DateTimeOffset stamp = DateTimeOffset.UtcNow;
        List<ChatMessage> Answers() => [.. Enumerable.Range(60, 40).Select(number => new ChatMessage { Role = MessageRole.Assistant, Text = $"a{number}\n\n{tall}", Time = stamp })];
        List<ChatMessage> WithWork() => [.. Answers().SelectMany(answer => new[] { new ChatMessage { Role = MessageRole.Assistant, Text = $"Read {answer.Text[..3]}.txt", IsWork = true, Time = answer.Time }, new ChatMessage { Role = MessageRole.Assistant, Text = "→ 12 lines", IsWork = true, Time = answer.Time }, answer, new ChatMessage { Role = MessageRole.Assistant, Text = $"Run step {answer.Text[..3]}", IsWork = true, Time = answer.Time } })];
        int expected = turningOn ? WithWork().Count : Answers().Count;
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = turningOn ? Answers() : WithWork(), ShowWork = !turningOn });

        (double moved, int shown, int firstVisible) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 700, Content = view };
            window.Show();

            void Settle(Func<bool> done)
            {
                for (int attempt = 0; attempt < 300 && !done(); attempt++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(10);
                }

                for (int round = 0; round < 5; round++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(20);
                }
            }

            ChatViewModel chat = model.Active!.Chat;
            Settle(() => chat.Messages.Count == (turningOn ? Answers().Count : WithWork().Count));
            chat.AutoScroll = false;
            ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "MessageScroller");
            Settle(() => scroller.Extent.Height > scroller.Viewport.Height * 2);
            Control OnScreen(string start) => window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage message && message.Text.StartsWith(start, StringComparison.Ordinal));
            double Y(string start) => Avalonia.VisualExtensions.TranslatePoint(OnScreen(start), new Avalonia.Point(0, 0), scroller)!.Value.Y;

            scroller.Offset = new Avalonia.Vector(0, scroller.Extent.Height / 2);
            Settle(() => false || scroller.Offset.Y > 0);
            Control first = window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage { IsWork: false } message && message.Text.StartsWith('a') && Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(0, 0), scroller)!.Value.Y + control.Bounds.Height > 0);
            string keep = ((ChatMessage)first.DataContext!).Text[..3];
            double before = Y(keep);

            connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = turningOn ? WithWork() : Answers(), ShowWork = turningOn });
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspace.Id, ShowWork = turningOn });
            Settle(() => chat.Messages.Count == expected);
            double after = Y(keep);
            window.Close();
            return (after - before, chat.Messages.Count, int.Parse(keep[1..], CultureInfo.InvariantCulture));
        });

        Assert.Equal(expected, shown);
        Assert.InRange(firstVisible, 60, 99);
        Assert.InRange(moved, -1.5, 1.5);
    }

    [Fact]
    public async Task HomeView_KeepsTheMessageAtTheTopOfTheScreenInPlaceThroughSeveralChunksLoadedOneAfterTheOtherWhileTheUserKeepsScrollingUp()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        string tall = string.Join("\n\n", Enumerable.Range(0, 5).Select(line => $"Paragraph {line} of a long answer that wraps over a few lines when the chat is narrow enough."));
        List<ChatMessage> Chunk(int begin, int end) => [.. Enumerable.Range(begin, end - begin).Select(number => new ChatMessage { Role = MessageRole.Assistant, Text = $"a{number}\n\n{tall}" })];
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Chunk(80, 100), Older = "80" });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.Is<string?>(cursor => cursor != null), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string? cursor, int? _, int? characters, CancellationToken _) =>
        {
            Thread.Sleep(30);
            int end = int.Parse(cursor!, CultureInfo.InvariantCulture);
            int begin = Math.Max(0, end - 20);
            return new ChatHistory { Messages = Chunk(begin, end), Older = begin > 0 ? begin.ToString(CultureInfo.InvariantCulture) : null };
        });

        double[] moves = await fixture.Run<double[]>(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 700, Content = view };
            window.Show();

            void Pump(int rounds)
            {
                for (int round = 0; round < rounds; round++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(20);
                }
            }

            ChatViewModel chat = model.Active!.Chat;
            Pump(20);
            chat.AutoScroll = false;
            ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "MessageScroller");
            scroller.ScrollToEnd();
            Pump(10);
            Control OnScreen(string start) => window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage message && message.Text.StartsWith(start, StringComparison.Ordinal));
            double Y(string start) => Avalonia.VisualExtensions.TranslatePoint(OnScreen(start), new Avalonia.Point(0, 0), scroller)!.Value.Y;
            List<double> moved = [];

            foreach (int first in new[] { 80, 60, 40 })
            {
                scroller.Offset = new Avalonia.Vector(0, 0);
                Pump(3);
                double before = Y($"a{first}");
                int count = chat.Messages.Count;

                for (int wait = 0; wait < 100 && chat.Messages.Count == count; wait++)
                {
                    Pump(1);
                }

                Pump(8);
                moved.Add(Y($"a{first}") - before);
            }

            window.Close();
            return [.. moved];
        });

        Assert.All(moves, move => Assert.InRange(move, -1.5, 1.5));
    }

    private List<ChatMessage> Numbered(int begin, int end)
    {
        string tall = string.Join("\n\n", Enumerable.Range(0, 5).Select(line => $"Paragraph {line} of a long answer that wraps over a few lines when the chat is narrow enough."));
        string rich = "## Heading\n\n- one item\n- another item\n\n```csharp\nint x = 1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n> a quote";
        return [.. Enumerable.Range(begin, end - begin).Select(number => new ChatMessage { Role = MessageRole.Assistant, Text = $"a{number}\n\n{(number % 3 == 0 ? rich : number % 3 == 1 ? tall : "short")}", Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(number) })];
    }

    // Shows a chat of the messages a80 to a99, with older ones to load twenty at a time after a delay as a server takes, and hands the view to the actions of a test.
    private async Task<T> InALongChat<T>(Func<ChatViewModel, ScrollViewer, Window, WorkspaceInfo, Action<int>, T> actions)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new ChatHistory { Messages = Numbered(80, 100), Older = "80" });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.Is<string?>(cursor => cursor != null), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(async (Guid _, string? cursor, int? _, int? _, CancellationToken _) =>
        {
            await Task.Delay(30);
            int end = int.Parse(cursor!, CultureInfo.InvariantCulture);
            int begin = Math.Max(0, end - 20);
            return new ChatHistory { Messages = Numbered(begin, end), Older = begin > 0 ? begin.ToString(CultureInfo.InvariantCulture) : null };
        });

        return await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 700, Content = view };
            window.Show();

            void Pump(int rounds)
            {
                for (int round = 0; round < rounds; round++)
                {
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(10);
                }
            }

            Pump(30);
            ScrollViewer scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "MessageScroller");
            T result = actions(model.Active!.Chat, scroller, window, workspace, Pump);
            window.Close();
            return result;
        });
    }

    private bool IsAtEnd(ScrollViewer scroller) => scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2;

    // The user scrolls up in steps, as with a wheel.
    private void ScrollUp(ScrollViewer scroller, Action<int> pump, int steps)
    {
        for (int step = 0; step < steps; step++)
        {
            scroller.Offset = new Avalonia.Vector(0, Math.Max(0, scroller.Offset.Y - 200));
            pump(5);
        }
    }

    [Fact]
    public async Task HomeView_LetsTheUserScrollUpThroughEveryChunkWithAutoScrollOnWithoutBeingTakenToTheEnd()
    {
        (int times, int shown, bool more, bool auto) = await InALongChat((chat, scroller, window, _, pump) =>
        {
            bool started = IsAtEnd(scroller);
            int taken = started ? 0 : 1;

            for (int step = 0; step < 200 && (chat.HasMore || scroller.Offset.Y > 1); step++)
            {
                ScrollUp(scroller, pump, 1);

                // Text that wraps anew grows the chat without anything new in it.
                if (step % 25 == 12)
                {
                    window.Width = window.Width > 1100 ? 1000 : 1200;
                    pump(8);
                }

                taken += IsAtEnd(scroller) ? 1 : 0;
            }

            return (taken, chat.Messages.Count, chat.HasMore, chat.AutoScroll);
        });

        Assert.Equal((0, 100, false, true), (times, shown, more, auto));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task HomeView_GoesToTheEndForNewOutputOnlyWhileAutoScrollIsOn(bool autoScroll, bool streamed)
    {
        (bool before, bool after) = await InALongChat((chat, scroller, _, workspace, pump) =>
        {
            chat.AutoScroll = autoScroll;

            if (streamed)
            {
                chat.ShowWork = true;
                pump(5);
            }

            ScrollUp(scroller, pump, 30);
            bool left = IsAtEnd(scroller);
            connection.Raise(x => x.EventReceived += null, streamed
                ? new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspace.Id, Text = "a piece of what is being written" }
                : new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Message = new ChatMessage { Role = MessageRole.Assistant, Text = "new output" } });
            pump(20);
            return (left, IsAtEnd(scroller));
        });

        Assert.False(before);
        Assert.Equal(autoScroll, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeView_StaysPutWhenTheChatIsReadAgainUnlessItHoldsSomethingNew(bool somethingNew)
    {
        (double moved, bool end) = await InALongChat((chat, scroller, window, workspace, pump) =>
        {
            ScrollUp(scroller, pump, 40);
            int count = chat.Messages.Count;
            Control kept = window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage && control.GetVisualParent() is Panel && Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(0, 0), scroller)!.Value.Y + control.Bounds.Height > 0);
            string name = ((ChatMessage)kept.DataContext!).Text.Split('\n')[0];
            double Y() => Avalonia.VisualExtensions.TranslatePoint(window.GetVisualDescendants().OfType<Control>().First(control => control.DataContext is ChatMessage message && message.Text.Split('\n')[0] == name && control.GetVisualParent() is Panel), new Avalonia.Point(0, 0), scroller)!.Value.Y;
            double before = Y();

            if (somethingNew)
            {
                connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [.. Numbered(81, 100), new ChatMessage { Role = MessageRole.Assistant, Text = "written while away" }], Older = "81" });
            }

            // As after a connection that was lost.
            Task read = chat.Resync();

            for (int round = 0; round < 100 && !read.IsCompleted; round++)
            {
                pump(1);
            }

            pump(20);
            return (somethingNew ? 0 : Y() - before, IsAtEnd(scroller));
        });

        Assert.Equal(somethingNew, end);
        Assert.InRange(moved, -1.5, 1.5);
    }

    [Fact]
    public async Task HomeView_ShowsTheLocalTimeUnderEachMessageThatHasOne()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        DateTimeOffset written = DateTimeOffset.UtcNow.AddMinutes(-3);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory
        {
            Messages =
            [
                new ChatMessage { Role = MessageRole.User, Text = "hello", Time = written },
                new ChatMessage { Role = MessageRole.Assistant, Text = "hi there", Time = written },
                new ChatMessage { Role = MessageRole.Assistant, Text = "no time" }
            ]
        });

        (string[] found, bool userStampOnTheLeft) stamps = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 800, Content = view };
            window.Show();

            for (int attempt = 0; attempt < 100 && model.Active!.Chat.Messages.Count < 3; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TextBlock[] blocks = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.FontSize == 11 && block.Classes.Contains("muted"))];
            string[] found = [.. blocks.Select(block => block.Text ?? string.Empty)];
            Border bubble = blocks[0].FindAncestorOfType<Border>()!;
            Avalonia.Point stampAt = Avalonia.VisualExtensions.TranslatePoint(blocks[0], new Avalonia.Point(0, 0), bubble)!.Value;
            bool userStampOnTheLeft = blocks[0].HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left && stampAt.X < 24 && bubble.Bounds.Width - stampAt.X - blocks[0].Bounds.Width > 24;
            window.Close();
            return (found, userStampOnTheLeft);
        });

        string expected = written.ToStamp(DateTimeOffset.UtcNow);
        Assert.Equal([expected, expected], stamps.found);
        Assert.True(stamps.userStampOnTheLeft);
    }

    [Fact]
    public async Task HomeView_ShowsTheNewWindowButtonRightOfTheWorkspacesButtonOnlyWhenTheClientCanOpenWindows()
    {
        Mock<INewWindow> newWindow = new();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });

        (bool without, bool with, bool rightOfMenu, bool opened) = await fixture.Run(() =>
        {
            bool Shown(HomeViewModel model, out double left, out double menu)
            {
                HomeView view = new() { DataContext = model };
                Window window = new() { Width = 900, Height = 600, Content = view };
                window.Show();
                TouchButton? button = window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(item => item.IsEffectivelyVisible && ToolTip.GetTip(item)?.ToString() == "Open a new window");
                TouchButton menuButton = window.GetVisualDescendants().OfType<TouchButton>().First(item => item.Content?.ToString() == "\u2630");
                left = button is null ? 0 : Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(), window)!.Value.X;
                menu = Avalonia.VisualExtensions.TranslatePoint(menuButton, new Avalonia.Point(), window)!.Value.X;
                bool visible = button is not null;

                if (button is not null)
                {
                    button.Command?.Execute(null);
                }

                window.Close();
                return visible;
            }

            HomeViewModel plain = new(connection.Object, ClientLayout.Panels);
            plain.Start().GetAwaiter().GetResult();
            HomeViewModel capable = new(connection.Object, ClientLayout.Panels, null, null, null, null, null, null, newWindow.Object);
            capable.Start().GetAwaiter().GetResult();
            bool without = Shown(plain, out _, out _);
            bool with = Shown(capable, out double left, out double menu);
            return (without, with, left > menu, newWindow.Invocations.Count > 0);
        });

        Assert.False(without);
        Assert.True(with);
        Assert.True(rightOfMenu);
        Assert.True(opened);
    }

    [Fact]
    public async Task HomeView_SendsOnShiftEnterAndStartsANewLineOnEnter()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        List<string> sent = [];
        connection.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, text, _) => sent.Add(text)).Returns(Task.CompletedTask);

        (string afterEnter, int sentByEnter, int sentByShiftEnter, string afterShiftEnter) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            TextBox box = window.GetVisualDescendants().OfType<TextBox>().First(item => item.Name == "ChatInput");
            box.Focus();
            model.Active!.Chat.Input = "hello";
            box.CaretIndex = 5;
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            string afterEnter = model.Active.Chat.Input;
            int sentByEnter = sent.Count;
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.Enter, null);

            for (int attempt = 0; attempt < 100 && sent.Count == 0; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            string afterShiftEnter = model.Active.Chat.Input;
            window.Close();
            return (afterEnter, sentByEnter, sent.Count, afterShiftEnter);
        });

        Assert.Equal("hello\n", afterEnter.Replace("\r\n", "\n"));
        Assert.Equal((0, 1), (sentByEnter, sentByShiftEnter));
        Assert.Equal(string.Empty, afterShiftEnter);
        Assert.StartsWith("hello", sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HomeView_ShowsABlueReviewActionButtonWhereTheSendButtonIsWhileNothingIsTypedAndSendsTheReviewPrompt()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { ExpandPrompt = "my expand", GlobalReviewPrompt = "review workspace and fix any problems or inconsistencies", GlobalExpandPrompt = "global expand" });
        string? prompted = null;
        connection.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, text, _) => prompted = text).Returns(Task.CompletedTask);

        (bool emptyReview, bool emptySend, bool typedReview, bool typedSend, bool reviewIsAction, string? sent, bool expandNextToReview, bool expandHiddenWhenTyped, string? expanded) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            TouchButton? Review() => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString()?.StartsWith("Review the workspace", StringComparison.Ordinal) == true);
            TouchButton? Send() => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && button.Content?.ToString() == "\u2191");
            bool emptyReview = Review() is not null;
            bool emptySend = Send() is not null;
            bool reviewIsAction = Review() is { } review && review.Classes.Contains("primary") && !review.Classes.Contains("icon") && review.Width == 34 && review.Height == 34 && ToolTip.GetPlacement(review) == PlacementMode.Top && review.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any();

            model.Active!.Chat.ReviewCommand.Execute(null);
            for (int attempt = 0; attempt < 100 && prompted is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            string? reviewed = prompted;
            prompted = null;
            TouchButton? Expand() => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString()?.StartsWith("Expand upon", StringComparison.Ordinal) == true);
            Avalonia.Point reviewAt = Avalonia.VisualExtensions.TranslatePoint(Review()!, new Avalonia.Point(), window)!.Value;
            Avalonia.Point expandAt = Avalonia.VisualExtensions.TranslatePoint(Expand()!, new Avalonia.Point(), window)!.Value;
            bool expandNextToReview = expandAt.X > reviewAt.X && expandAt.X - reviewAt.X < 60 && Math.Abs(expandAt.Y - reviewAt.Y) < 1 && Expand()!.Width == 34 && Expand()!.Classes.Contains("primary");
            model.Active.Chat.ExpandCommand.Execute(null);
            for (int attempt = 0; attempt < 100 && prompted is null; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            string? expanded = prompted;
            model.Active.Chat.Input = "check the parser";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool typedReview = Review() is not null;
            bool typedSend = Send() is not null;
            bool expandHiddenWhenTyped = Expand() is null;
            window.Close();
            return (emptyReview, emptySend, typedReview, typedSend, reviewIsAction, reviewed, expandNextToReview, expandHiddenWhenTyped, expanded);
        });

        Assert.Equal((true, false, false, true, true), (emptyReview, emptySend, typedReview, typedSend, reviewIsAction));
        Assert.Equal("review workspace and fix any problems or inconsistencies", sent);
        Assert.True(expandNextToReview);
        Assert.True(expandHiddenWhenTyped);
        Assert.Equal("my expand", expanded);
    }

    [Fact]
    public async Task HomeView_StartsALoopFromSendOptionsAndShowsItInTheHeaderWithAButtonToCancelIt()
    {
        WorkspaceInfo workspace = Repository();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it" });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "Session", PercentUsed = 10, WindowMinutes = 300 }] });

        (bool[] idle, int conditions, bool[] fields, bool[] looping, bool repeat, bool[] working) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, 900, ClientLayout.Tabs);
            HomeViewModel model = screen.Model;
            Window window = screen.Window;
            TouchButton? Find(string tip) => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString()?.StartsWith(tip, StringComparison.Ordinal) == true);
            bool Indicator() => window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(path => path.Name == "LoopIndicator" && path.IsEffectivelyVisible);
            bool[] idle = [Find("Review the workspace") is not null && Find("Resume: ask") is not null && Find("Pause: ask") is null, Find("Loop: send") is not null, Find("Send Options") is not null, Find("Cancel the loop") is not null, Indicator()];

            RightClick(window, screen.Named<TouchButton>("ReviewButton"));
            screen.Settle();
            SendOptionsViewModel panel = model.Active!.SendOptions;
            bool opened = panel.IsOpen && panel.IsNow && screen.Named<TextBlock>("SubmissionTitle").Text == "Review" && screen.Named<TextBlock>("SubmissionText").Text == "review it";
            panel.IsRepeat = true;
            screen.Settle();
            Grid shown = screen.Named<Grid>("SendOptionsPanel");
            bool Shown<T>() where T : Avalonia.Visual => shown.GetVisualDescendants().OfType<T>().Any(item => item.IsEffectivelyVisible);
            int conditions = panel.Loop.Conditions.Count;
            // What is submitted is shown and cannot be changed: nothing offers another prompt or a box to write one.
            bool fixedPrompt = !shown.GetVisualDescendants().OfType<RadioButton>().Any(radio => radio.IsEffectivelyVisible && radio.Content is "Review" or "Expand" or "Custom") && !shown.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.AcceptsReturn);
            panel.Loop.SelectedCondition = panel.Loop.Conditions.Single(choice => choice.Condition == LoopCondition.UntilTime);
            screen.Settle();
            bool timePicker = Shown<TimePicker>();
            panel.Loop.SelectedCondition = panel.Loop.Conditions.Single(choice => choice.Condition == LoopCondition.UntilWindow);
            screen.Settle();
            bool windowPicker = shown.GetVisualDescendants().OfType<ComboBox>().Any(box => box.IsEffectivelyVisible && box.ItemsSource is IReadOnlyList<string>);
            bool[] fields = [opened, fixedPrompt, timePicker, windowPicker, Equals(screen.Named<Button>("SendOptionsConfirm").Content, "Start Loop")];
            panel.CancelCommand.Execute(null);

            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", IntervalSeconds = 600, Condition = LoopCondition.Runs, Runs = 4 }, StartedAt = DateTimeOffset.UtcNow, Runs = 2 } });
            screen.Settle();
            bool[] looping = [Find("Cancel the loop") is not null, Indicator(), model.Active.LoopTip.Contains("2 runs so far", StringComparison.Ordinal)];
            model.Active.OpenSendOptionsCommand.Execute(null);
            screen.Settle();
            bool repeat = shown.GetVisualDescendants().OfType<RadioButton>().Single(radio => Equals(radio.Content, "Repeat")).IsEffectivelyEnabled;
            panel.CancelCommand.Execute(null);
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id });
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
            screen.Settle();
            bool[] working = [Find("Stop the work") is not null, Find("Review the workspace") is not null, Find("Pause: ask") is { } pause && pause.Classes.Contains("primary"), Find("Resume: ask") is not null];
            return (idle, conditions, fields, looping, repeat, working);
        });

        Assert.Equal([true, false, false, false, false], idle);
        Assert.Equal(5, conditions);
        Assert.Equal([true, true, true, true, true], fields);
        Assert.Equal([true, true, true], looping);
        Assert.False(repeat);
        Assert.Equal([true, true, true, false], working);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("SendButton", "do it", false, "Custom", "do it")]
    [InlineData("SendButton", "do it", true, "Custom", "do it")]
    [InlineData("ReviewButton", "", false, "Review", "review it")]
    [InlineData("ReviewButton", "", true, "Review", "review it")]
    [InlineData("ExpandButton", "", false, "Expand", "expand it")]
    [InlineData("ExpandButton", "", true, "Expand", "expand it")]
    public async Task MessageBox_ARightClickOrAHeldFingerOnASubmitButtonOpensSendOptionsForWhatItSubmitsAndSubmitsNothing(string name, string typed, bool touch, string kind, string submitted)
    {
        WorkspaceInfo workspace = Repository();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it", GlobalExpandPrompt = "expand it" });

        (bool open, bool now, string shownKind, string shownText, string input) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, 390, ClientLayout.Tabs);
            screen.Model.Active!.Chat.Input = typed;
            screen.Settle();
            TouchButton button = screen.Named<TouchButton>(name);
            SendOptionsViewModel panel = screen.Model.Active.SendOptions;

            if (touch)
            {
                button.HoldDelay = TimeSpan.FromMilliseconds(30);
                Avalonia.Input.Pointer pointer = Finger();
                Press(button, screen.Window, pointer);
                Wait(() => panel.IsOpen, 10000);
                screen.Settle();
                Lift(button, screen.Window, pointer);
            }
            else
            {
                RightClick(screen.Window, button);
            }

            Wait(() => panel.HasSubmissionText, 10000);
            screen.Settle();
            return (panel.IsOpen, panel.IsNow, screen.Named<TextBlock>("SubmissionTitle").Text!, screen.Named<TextBlock>("SubmissionText").Text!, screen.Model.Active.Chat.Input);
        });

        Assert.Equal((true, true, kind, submitted, typed), (open, now, shownKind, shownText, input));
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.QueueInput(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("shift", false, "sent")]
    [InlineData("alt", false, "queued")]
    [InlineData("alt", true, "queued")]
    [InlineData("shift", true, "options")]
    [InlineData("button", true, "options")]
    [InlineData("button", false, "sent")]
    public async Task MessageBox_ShiftEnterAndTheSendButtonSendNowOrOfferTheOtherWaysAndAltEnterQueuesToCommit(string how, bool gitWorks, string happens)
    {
        WorkspaceInfo workspace = Repository();
        List<string> did = [];
        connection.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => did.Add("sent")).Returns(Task.CompletedTask);
        connection.Setup(x => x.QueueInput(workspace.Id, "do it", true, It.IsAny<CancellationToken>())).Callback(() => did.Add("queued")).ReturnsAsync([]);

        if (gitWorks)
        {
            connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main", Busy = true });
            connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        }

        (string shown, bool open, string[] problems, string note) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, 390, ClientLayout.Tabs);
            Wait(() => !gitWorks || screen.Model.Active!.State == WorkspaceState.Managing, 10000);
            TextBox box = screen.Named<TextBox>("ChatInput");
            screen.Model.Active!.Chat.Input = "do it";
            screen.Settle();
            TouchButton send = screen.Named<TouchButton>("SendButton");
            string shown = $"{screen.Model.Active.State}|{send.IsEffectivelyVisible}|{send.IsEffectivelyEnabled}|{screen.Model.Active.Chat.BlockReason}";
            SendOptionsViewModel panel = screen.Model.Active.SendOptions;

            if (how == "button")
            {
                screen.Window.MouseDown(Center(screen.Window, send), Avalonia.Input.MouseButton.Left);
                screen.Window.MouseUp(Center(screen.Window, send), Avalonia.Input.MouseButton.Left);
            }
            else
            {
                box.Focus();
                box.CaretIndex = 5;
                screen.Window.KeyPress(Avalonia.Input.Key.Enter, how == "shift" ? Avalonia.Input.RawInputModifiers.Shift : Avalonia.Input.RawInputModifiers.Alt, Avalonia.Input.PhysicalKey.Enter, null);
            }

            Wait(() => did.Count > 0 || panel.IsOpen, 10000);
            screen.Settle();
            string[] problems = panel.IsOpen ? screen.Misplaced(screen.Named<Border>("SendOptionsCard")) : [];
            return (shown, panel.IsOpen, problems, panel.IsOpen ? screen.Named<TextBlock>("SendOptionsUnavailable").Text! : string.Empty);
        });

        Assert.Equal(gitWorks ? "Managing|True|True|Git is working, wait for it to finish" : "Ready|True|True|", shown);
        Assert.Equal(happens == "options", open);
        Assert.Equal(happens == "options" ? [] : [happens], did);
        Assert.Empty(problems);
        Assert.Equal(open ? "This chat cannot take it now: git is working, wait for it to finish." : string.Empty, note);
    }

    [Theory]
    [InlineData("enter", "sent", false)]
    [InlineData("enter on a choice", "queued", false)]
    [InlineData("enter on cancel", "", false)]
    [InlineData("escape", "", false)]
    [InlineData("shift enter", "", true)]
    [InlineData("enter in the minutes", "looped 7", false)]
    [InlineData("enter in an open list", "", true)]
    public async Task SendOptions_EnterStartsWhatIsChosenAndEscapeCancelsWhileThePanelHasTheKeyboard(string how, string happens, bool stays)
    {
        WorkspaceInfo workspace = Repository();
        List<string> did = [];
        connection.Setup(x => x.Prompt(workspace.Id, "do it", It.IsAny<CancellationToken>())).Callback(() => did.Add("sent")).Returns(Task.CompletedTask);
        connection.Setup(x => x.QueueInput(workspace.Id, "do it", true, It.IsAny<CancellationToken>())).Callback(() => did.Add("queued")).ReturnsAsync([]);
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Callback((Guid _, LoopSettings settings, CancellationToken _) => did.Add($"looped {settings.IntervalSeconds / 60}")).Returns(Task.CompletedTask);

        (bool tookKeyboard, bool open, bool gaveBack, string input) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, 1200);
            TextBox box = screen.Named<TextBox>("ChatInput");
            screen.Model.Active!.Chat.Input = "do it";
            screen.Settle();
            box.Focus();
            screen.Model.Active.OpenSendOptionsCommand.Execute(null);
            screen.Settle();
            SendOptionsViewModel panel = screen.Model.Active.SendOptions;
            Border card = screen.Named<Border>("SendOptionsCard");
            Wait(() => card.IsFocused, 10000);
            bool tookKeyboard = card.IsFocused;
            Avalonia.Input.Key key = how == "escape" ? Avalonia.Input.Key.Escape : Avalonia.Input.Key.Enter;
            Avalonia.Input.RawInputModifiers modifiers = how == "shift enter" ? Avalonia.Input.RawInputModifiers.Shift : Avalonia.Input.RawInputModifiers.None;

            switch (how)
            {
                case "enter on a choice":
                    RadioButton next = card.GetVisualDescendants().OfType<RadioButton>().Single(radio => Equals(radio.Content, "Next"));
                    next.Focus();
                    next.IsChecked = true;
                    break;
                case "enter on cancel":
                    screen.Named<TouchButton>("SendOptionsCancel").Focus();
                    break;
                case "enter in the minutes":
                    panel.IsRepeat = true;
                    screen.Settle();
                    TextBox minutes = card.GetVisualDescendants().OfType<NumericUpDown>().First(item => item.IsEffectivelyVisible).GetVisualDescendants().OfType<TextBox>().Single();
                    minutes.Focus();
                    minutes.SelectAll();
                    screen.Window.KeyTextInput("7");
                    break;
                case "enter in an open list":
                    panel.IsRepeat = true;
                    screen.Settle();
                    ComboBox ends = card.GetVisualDescendants().OfType<ComboBox>().First(item => item.IsEffectivelyVisible);
                    ends.Focus();
                    ends.IsDropDownOpen = true;
                    screen.Settle();
                    ((Control)ends.ContainerFromIndex(1)!).Focus();
                    break;
            }

            screen.Settle();
            screen.Window.KeyPress(key, modifiers, key == Avalonia.Input.Key.Escape ? Avalonia.Input.PhysicalKey.Escape : Avalonia.Input.PhysicalKey.Enter, null);
            Wait(() => !panel.IsOpen, stays ? 150 : 10000);
            screen.Settle();

            if (!panel.IsOpen)
            {
                Wait(() => box.IsFocused, 10000);
            }

            return (tookKeyboard, panel.IsOpen, box.IsFocused, screen.Model.Active.Chat.Input);
        });

        Assert.True(tookKeyboard);
        Assert.Equal((stays, !stays), (open, gaveBack));
        Assert.Equal(happens.Length > 0 ? [happens] : [], did);
        Assert.Equal(happens.Length > 0 ? string.Empty : "do it", input);
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(390, false)]
    public async Task SendOptions_ABoxWithLinesStartsANewOneOnShiftEnterAndOnAPhoneOnEnterToo(int width, bool computer)
    {
        WorkspaceInfo workspace = Repository();
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmStatus { Server = new SwarmServer { Name = "here" }, Peers = [new SwarmPeer { Id = "p", Name = "box", Fingerprint = "f", Mode = SwarmMode.Direct, SendWork = true }] });
        List<SwarmTaskRequest> delegated = [];
        connection.Setup(x => x.DelegateTask(workspace.Id, It.IsAny<SwarmTaskRequest>(), It.IsAny<CancellationToken>())).Callback((Guid _, SwarmTaskRequest request, CancellationToken _) => delegated.Add(request)).ReturnsAsync(new SwarmStatus { Server = new SwarmServer(), Peers = [] });

        (string typed, bool open) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, computer ? ClientLayout.Panels : ClientLayout.Tabs);
            screen.Model.Active!.Chat.Input = "port it";
            screen.Settle();
            screen.Model.Active.OpenSendOptionsCommand.Execute(null);
            screen.Settle();
            SendOptionsViewModel panel = screen.Model.Active.SendOptions;
            // The panel takes the keyboard once it shows, which must not happen after the box was chosen.
            Border card = screen.Named<Border>("SendOptionsCard");
            Wait(() => card.IsFocused, 10000);
            panel.IsNewSession = true;
            Wait(() => panel.Targets.Count > 1, 10000);
            panel.Target = panel.Targets[1];
            panel.Name = "docs";
            screen.Settle();
            TextBox expected = card.GetVisualDescendants().OfType<TextBox>().Single(found => found.PlaceholderText == "Expected result and how to check it");
            expected.Focus();
            screen.Window.KeyTextInput("one");
            screen.Window.KeyPress(Avalonia.Input.Key.Enter, computer ? Avalonia.Input.RawInputModifiers.Shift : Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            screen.Window.KeyTextInput("two");
            screen.Settle();
            string typed = expected.Text!.Replace("\r\n", "\n");

            if (computer)
            {
                screen.Window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                Wait(() => !panel.IsOpen, 10000);
            }

            return (typed, panel.IsOpen);
        });

        Assert.Equal(("one\ntwo", !computer), (typed, open));
        Assert.Equal(computer ? ["one\ntwo"] : [], delegated.Select(request => request.ExpectedResult!.Replace("\r\n", "\n")));
    }

    [Theory]
    [InlineData("tap", true, 1, 0)]
    [InlineData("hold", true, 0, 1)]
    [InlineData("drag", true, 0, 0)]
    [InlineData("scroll", true, 0, 0)]
    [InlineData("hold", false, 1, 0)]
    [InlineData("right click", true, 0, 1)]
    [InlineData("right click", false, 0, 0)]
    [InlineData("menu key", true, 0, 1)]
    [InlineData("click", true, 1, 0)]
    [InlineData("right click after a hold", true, 0, 2)]
    public async Task TouchButton_GivesItsOptionsForARightClickOrAHeldFingerAndClicksOtherwise(string gesture, bool offered, int clicks, int options)
    {
        (int clicked, int asked) = await fixture.Run(() =>
        {
            int clicked = 0;
            int asked = 0;
            TouchButton button = new() { Content = "x", Width = 100, Height = 40, OptionsCommand = new AsyncCommand(_ => { asked++; return Task.CompletedTask; }, () => offered, false), HoldDelay = TimeSpan.FromMilliseconds(30) };
            button.Click += (_, _) => clicked++;
            Window window = new() { Width = 300, Height = 100, Content = button };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Input.Pointer pointer = Finger();

            // A hold that has options to give is waited for, however late a busy machine gets to it.
            void Hold()
            {
                Press(button, window, pointer);

                if (offered)
                {
                    Wait(() => asked > 0, 10000);
                }
                else
                {
                    Wait(() => false, 150);
                }

                Lift(button, window, pointer);
            }

            switch (gesture)
            {
                case "tap":
                    Press(button, window, pointer);
                    Lift(button, window, pointer);
                    Wait(() => false, 150);
                    break;
                case "hold":
                    Hold();
                    break;
                case "drag":
                    Press(button, window, pointer);
                    button.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, button, pointer, window, new Avalonia.Point(20, 45), 1, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None));
                    Wait(() => false, 150);
                    break;
                case "scroll":
                    Press(button, window, pointer);
                    button.RaiseEvent(new Avalonia.Input.PointerCaptureLostEventArgs(button, pointer));
                    Wait(() => false, 150);
                    break;
                case "right click":
                    RightClick(window, button);
                    break;
                case "menu key":
                    button.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                    break;
                case "right click after a hold":
                    Hold();
                    RightClick(window, button);
                    break;
                default:
                    window.MouseDown(Center(window, button), Avalonia.Input.MouseButton.Left);
                    window.MouseUp(Center(window, button), Avalonia.Input.MouseButton.Left);
                    break;
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Close();
            return (clicked, asked);
        });

        Assert.Equal((clicks, options), (clicked, asked));
    }

    [Fact]
    public async Task HomeView_ShowsAnEqualHistoryTabOnlyWhenTheWorkspaceIsAGitRepository()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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

        Assert.Equal(["Chat", "Files", "Script", "Activity", "History"], labels);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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

        Assert.Equal(["Chat", "Script", "Activity", "History"], labels);
        Assert.Equal(1, controls);
        Assert.Equal(4, graphs);
        Assert.Equal([0, 0, 1, 0], lanes);
        Assert.Contains("hello", output);
    }

    [Theory]
    [InlineData(false, 3, true)]
    [InlineData(true, 3, false)]
    [InlineData(false, 25, false)]
    public async Task TouchButton_ClicksOnATapButNeverCapturesTheFingerSoDraggingFromItScrolls(bool scrolls, int movement, bool clicks)
    {
        (bool captured, int clicked) = await fixture.Run(() =>
        {
            int count = 0;
            TouchButton button = new() { Content = "x", Width = 100, Height = 40 };
            button.Click += (_, _) => count++;
            Window window = new() { Width = 300, Height = 100, Content = button };
            window.Show();
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);
            Avalonia.Input.PointerPointProperties properties = new(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            button.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(button, pointer, window, new Avalonia.Point(20, 20), 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });
            bool captured = pointer.Captured is not null;
            Avalonia.Point end = new(20, 20 + movement);
            button.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, button, pointer, window, end, 1, properties, Avalonia.Input.KeyModifiers.None));

            if (scrolls)
            {
                button.RaiseEvent(new Avalonia.Input.PointerCaptureLostEventArgs(button, pointer));
            }

            button.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(button, pointer, window, end, 2, properties, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left) { RoutedEvent = Avalonia.Input.InputElement.PointerReleasedEvent });
            window.Close();
            return (captured, count);
        });

        Assert.False(captured);
        Assert.Equal(clicks ? 1 : 0, clicked);
    }

    private static Avalonia.Input.Pointer Finger() => new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);

    private static Avalonia.Point Center(Window window, Control control) => Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Press(Control control, Window window, Avalonia.Input.Pointer pointer) => control.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(control, pointer, window, Center(window, control), 0, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });

    private static void Lift(Control control, Window window, Avalonia.Input.Pointer pointer) => control.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(control, pointer, window, Center(window, control), 2, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left) { RoutedEvent = Avalonia.Input.InputElement.PointerReleasedEvent });

    private static void RightClick(Window window, Control control)
    {
        Avalonia.Point at = Center(window, control);
        window.MouseDown(at, Avalonia.Input.MouseButton.Right);
        window.MouseUp(at, Avalonia.Input.MouseButton.Right);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Wait(Func<bool> done, int milliseconds = 2000)
    {
        for (int waited = 0; waited < milliseconds && !done(); waited += 10)
        {
            Thread.Sleep(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilePreviews_ScrollBothWaysFromAnywhereWithTheSidewaysBarAlwaysInViewAndTheNumbersStayPut(bool diff)
    {
        string longLine = string.Join(' ', Enumerable.Repeat("word", 80));
        string text = string.Join('\n', Enumerable.Range(0, 120).Select(number => $"{number} {longLine}"));

        (double extentHeight, double viewportHeight, double extentWidth, double viewportWidth, double barBottom, double height, double shifted, int scrollers) = await fixture.Run(() =>
        {
            Control view = diff
                ? new DiffView { Diff = new GitDiff { Path = "a.txt", Lines = [.. Enumerable.Range(0, 120).Select(number => new GitDiffLine { Kind = GitDiffKind.Unchanged, OldNumber = number + 1, NewNumber = number + 1, Text = $"{number} {longLine}" })] } }
                : new SyntaxView { File = new FileContent { Path = "a.txt", Text = text } };
            Window window = new() { Width = 400, Height = 300, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ScrollViewer scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Avalonia.Controls.Primitives.ScrollBar bar = scroller.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().First(item => item.Orientation == Avalonia.Layout.Orientation.Horizontal);
            double barBottom = Avalonia.VisualExtensions.TranslatePoint(bar, new Avalonia.Point(0, bar.Bounds.Height), view)?.Y ?? -1;
            scroller.Offset = new Avalonia.Vector(60, 0);
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            double shifted = view.GetVisualDescendants().OfType<Border>().Select(border => border.RenderTransform).OfType<Avalonia.Media.TranslateTransform>().First().X;
            (double, double, double, double, double, double, double, int) result = (scroller.Extent.Height, scroller.Viewport.Height, scroller.Extent.Width, scroller.Viewport.Width, barBottom, view.Bounds.Height, shifted, view.GetVisualDescendants().OfType<ScrollViewer>().Count());
            window.Close();
            return result;
        });

        Assert.True(extentHeight > viewportHeight * 3);
        Assert.True(extentWidth > viewportWidth * 2);
        Assert.InRange(barBottom, height - 2, height + 2);
        Assert.Equal(60, shifted);
        Assert.Equal(1, scrollers);
    }

    [Fact]
    public async Task EveryPieceOfSelectableTextTheClientShowsIgnoresASingleTouch()
    {
        (int plain, int safe) = await fixture.Run(() =>
        {
            MarkdownView view = new() { Source = "# Title\n\nSome **bold** text\n\n```\ncode\n```\n\n| a | b |\n| - | - |\n| 1 | 2 |" };
            Window window = new() { Width = 400, Height = 400, Content = view };
            window.Show();
            List<SelectableTextBlock> blocks = [.. view.GetVisualDescendants().OfType<SelectableTextBlock>()];
            window.Close();
            return (blocks.Count(block => block is not ScrollSafeTextBlock), blocks.Count(block => block is ScrollSafeTextBlock));
        });

        Assert.Equal(0, plain);
        Assert.True(safe >= 4);
    }

    [Theory]
    [InlineData(Avalonia.Input.PointerType.Touch, 1, false, false, false, "")]
    [InlineData(Avalonia.Input.PointerType.Touch, 1, true, false, true, "some")]
    [InlineData(Avalonia.Input.PointerType.Touch, 1, true, true, false, "")]
    [InlineData(Avalonia.Input.PointerType.Touch, 2, false, false, false, "")]
    [InlineData(Avalonia.Input.PointerType.Touch, 3, false, false, false, "")]
    [InlineData(Avalonia.Input.PointerType.Touch, 2, true, false, true, "some")]
    [InlineData(Avalonia.Input.PointerType.Mouse, 1, false, false, true, "")]
    public async Task ScrollSafeTextBlock_SelectsOnAMouseOrATouchHeldStillButNotATouchThatIsTappedRepeatedlyOrScrolls(Avalonia.Input.PointerType type, int clicks, bool waits, bool moves, bool selects, string word)
    {
        (bool captured, string selected) = await fixture.Run(() =>
        {
            ScrollSafeTextBlock block = new() { Text = "some text to select", Width = 200, HoldDelay = TimeSpan.FromMilliseconds(30) };
            Window window = new() { Width = 300, Height = 100, Content = block };
            window.Show();
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), type, true);
            Avalonia.Input.PointerPointProperties properties = new(type == Avalonia.Input.PointerType.Mouse ? Avalonia.Input.RawInputModifiers.LeftMouseButton : Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            block.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(block, pointer, window, new Avalonia.Point(5, 5), 0, properties, Avalonia.Input.KeyModifiers.None, clicks) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });

            if (moves)
            {
                block.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, block, pointer, window, new Avalonia.Point(5, 40), 1, properties, Avalonia.Input.KeyModifiers.None));
            }

            if (waits)
            {
                Thread.Sleep(150);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            bool captured = pointer.Captured == block;
            string selected = block.SelectedText;
            window.Close();
            return (captured, selected);
        });

        Assert.Equal(selects, captured);
        Assert.Equal(word, selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrollSafeTextBlock_ATouchThatDragsOverTextThePlatformCapturedForItDoesNotSelect(bool released)
    {
        string selected = await fixture.Run(() =>
        {
            ScrollSafeTextBlock block = new() { Text = "some text\nto drag over\nand more text", Background = Avalonia.Media.Brushes.Transparent };
            Window window = new() { Width = 300, Height = 200, Content = block };
            window.Show();
            Avalonia.Input.PointerPointProperties properties = new(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);
            block.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(block, pointer, window, new Avalonia.Point(5, 5), 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });
            pointer.Capture(block);

            for (int y = 5; y < 50; y += 10)
            {
                block.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, block, pointer, window, new Avalonia.Point(60, y), 1, properties, Avalonia.Input.KeyModifiers.None));
            }

            if (released)
            {
                block.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(block, pointer, window, new Avalonia.Point(60, 50), 2, properties, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left) { RoutedEvent = Avalonia.Input.InputElement.PointerReleasedEvent });
            }

            string selected = block.SelectedText;
            window.Close();
            return selected;
        });

        Assert.Equal(string.Empty, selected);
    }

    [Fact]
    public async Task ScrollSafeTextBlock_ATapElsewhereDropsTheSelectionAHoldMadeButTouchingDownDoesNot()
    {
        (string held, string touched, string after) = await fixture.Run(() =>
        {
            ScrollSafeTextBlock first = new() { Text = "some text to select", HoldDelay = TimeSpan.FromMilliseconds(30) };
            ScrollSafeTextBlock second = new() { Text = "other text", HoldDelay = TimeSpan.FromMilliseconds(30) };
            Window window = new() { Width = 300, Height = 100, Content = new StackPanel { Children = { first, second } } };
            window.Show();
            Avalonia.Input.PointerPointProperties properties = new(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);
            first.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(first, pointer, window, new Avalonia.Point(5, 5), 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });
            Thread.Sleep(150);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string held = first.SelectedText;
            Avalonia.Input.Pointer other = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);
            second.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(second, other, window, new Avalonia.Point(5, 25), 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });
            string touched = first.SelectedText;
            second.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(second, other, window, new Avalonia.Point(5, 25), 1, properties, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left) { RoutedEvent = Avalonia.Input.InputElement.PointerReleasedEvent });
            string after = first.SelectedText;
            window.Close();
            return (held, touched, after);
        });

        Assert.Equal(("some", "some"), (held, touched));
        Assert.Equal(string.Empty, after);
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, false, true)]
    public async Task HomeView_LongTapRunsAScriptOnMobileButNotATapOrAMovingFinger(bool move, bool wait, bool runs, bool previews)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }] });
        connection.Setup(x => x.ReadFile(workspace.Id, "a.cs", It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = "a.cs", Text = "x" });

        (List<string> requested, string? previewed) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.Active!.Files.Start().GetAwaiter().GetResult();
            model.Active.ShowFilesCommand.Execute(null);
            List<string> requested = [];
            model.Active.Files.ScriptRequested += requested.Add;
            HomeView view = new() { DataContext = model, FileHoldDelay = TimeSpan.FromMilliseconds(30) };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TextBlock name = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == "a.cs" && block.IsEffectivelyVisible);
            Avalonia.Input.PointerPointProperties properties = new(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed);
            Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true);
            Avalonia.Point point = Avalonia.VisualExtensions.TranslatePoint(name, new Avalonia.Point(2, 2), window)!.Value;
            name.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(name, pointer, window, point, 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });

            if (move)
            {
                name.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, name, pointer, window, new Avalonia.Point(point.X, point.Y + 40), 1, properties, Avalonia.Input.KeyModifiers.None));
            }

            if (wait)
            {
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(100);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
            }

            name.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(name, pointer, window, point, 1, properties, Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left) { RoutedEvent = Avalonia.Input.InputElement.PointerReleasedEvent });
            string? previewed = model.Active.Files.Preview?.Path;
            window.Close();
            return (requested, previewed);
        });

        Assert.Equal(runs, requested.Contains("a.cs"));
        Assert.Equal(previews, previewed is not null);
    }

    [Fact]
    public async Task HomeView_ShowsBackAndForwardButtonsLeftOfTheFileBarThatStepThroughTheFilesOpened()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }, new FileEntry { Name = "b.cs", Path = "b.cs", IsFolder = false }] });
        connection.Setup(x => x.ReadFile(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string path, CancellationToken _) => new FileContent { Path = path, Text = "x" });

        (bool[] shownBefore, bool[] atTwo, string? afterBack, bool inOrder) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            model.Active!.Files.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1400, Height = 800, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TouchButton? Find(string tip) => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => ToolTip.GetTip(button)?.ToString() == tip && button.IsEffectivelyVisible);
            bool[] shownBefore = [Find("Back") is not null, Find("Forward") is not null];
            model.Active.Files.Choose(model.Active.Files.Nodes.Single(node => node.Path == "a.cs")).GetAwaiter().GetResult();
            model.Active.Files.Choose(model.Active.Files.Nodes.Single(node => node.Path == "b.cs")).GetAwaiter().GetResult();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] atTwo = [Find("Back")!.IsEffectivelyEnabled, Find("Forward")!.IsEffectivelyEnabled];
            Find("Back")!.Command!.Execute(null);

            for (int attempt = 0; attempt < 100 && model.Active.Files.Preview?.Path != "a.cs"; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            string? afterBack = model.Active.Files.Preview?.Path;
            double X(TouchButton button) => Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(), window)!.Value.X;
            bool inOrder = X(Find("Back")!) < X(Find("Forward")!) && X(Find("Forward")!) < X(Find("Close")!);
            window.Close();
            return (shownBefore, atTwo, afterBack, inOrder);
        });

        Assert.Equal([false, false], shownBefore);
        Assert.Equal([true, false], atTwo);
        Assert.Equal("a.cs", afterBack);
        Assert.True(inOrder);
    }

    [Fact]
    public async Task HomeView_GitPromptFocusesItsBoxEscapeCancelsAndEnterSubmits()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
    public async Task HomeView_MessageBoxShowsStopAndCancelInsteadOfReviewAndSendWhileWorkRunsAndAsksFirst()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
            string[] Shown() => [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Command == model.Active?.SubmitCommand ? "send" : button.Command == model.Active?.Chat.ReviewCommand ? "review" : button.Command == model.BeginStopCommand ? "stop" : string.Empty).Where(name => name.Length > 0)];
            string[] ready = Shown();
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
            string[] working = Shown();
            bool[] enabled = [model.BeginStopCommand.CanExecute(null)];
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

        Assert.Equal(["review"], ready);
        Assert.Equal(["review", "stop"], working);
        Assert.Equal([true], enabled);
        Assert.True(typing);
        Assert.True(asking);
        Assert.Equal("Keep Working", dismiss);
        Assert.True(askedAndNothingSent);
        connection.Verify(x => x.StopWorkspace(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HomeView_TabsLayoutHasNoWindowControlsButAScriptTab()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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

        Assert.Equal(["Chat", "Files", "Script", "Activity"], labels);
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

    [Fact]
    public async Task MainView_RedrawBuildsTheScreenAgainAndKeepsFollowingTheViewModel()
    {
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Name).Returns("Auto Remote");
        info.SetupGet(x => x.Version).Returns("1.0.0");
        MainViewModel model = new(info.Object, new Mock<IServerConnection.IFactory>().Object, new Mock<ISettingsStore>().Object);

        (bool rebuilt, bool sameTexts, bool stillBound) = await fixture.Run(() =>
        {
            model.Start().GetAwaiter().GetResult();
            MainView view = new() { DataContext = model };
            Window window = new() { Width = 390, Height = 800, Content = view };
            window.Show();
            ConnectView before = window.GetVisualDescendants().OfType<ConnectView>().Single();
            string[] textsBefore = [.. before.GetVisualDescendants().OfType<TouchButton>().Select(button => $"{button.Content}")];

            view.Redraw();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            ConnectView after = window.GetVisualDescendants().OfType<ConnectView>().Single();
            string[] textsAfter = [.. after.GetVisualDescendants().OfType<TouchButton>().Select(button => $"{button.Content}")];
            bool rebuilt = !ReferenceEquals(before, after);
            model.Start().GetAwaiter().GetResult();
            window.Close();
            return (rebuilt, textsBefore.SequenceEqual(textsAfter), model.Current is ConnectViewModel);
        });

        Assert.True(rebuilt);
        Assert.True(sameTexts);
        Assert.True(stillBound);
    }

    [Theory]
    [InlineData(ClientLayout.Tabs, 390, false, "")]
    [InlineData(ClientLayout.Tabs, 390, true, "")]
    [InlineData(ClientLayout.Tabs, 390, false, "typed")]
    [InlineData(ClientLayout.Panels, 1280, false, "")]
    [InlineData(ClientLayout.Panels, 1280, true, "")]
    public async Task HomeView_EveryIconIsCenteredBothWaysInItsButton(ClientLayout layout, int width, bool working, string typed)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(working ? WorkspaceState.Working : WorkspaceState.Ready);
        connection.Setup(x => x.ListFiles(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false, Size = 1 }] });

        (List<string> off, int checkedIcons) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, layout);
            model.Start().GetAwaiter().GetResult();
            model.Active!.Chat.Input = typed;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = width, Height = 900, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            List<string> off = [];
            int count = 0;

            foreach (Button button in window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
            {
                if (button.Content is not Avalonia.Controls.Shapes.Path path || path.RenderedGeometry is not { } rendered)
                {
                    continue;
                }

                count++;
                Avalonia.Point origin = Avalonia.VisualExtensions.TranslatePoint(path, new Avalonia.Point(0, 0), button)!.Value;
                double x = origin.X + rendered.Bounds.Center.X - button.Bounds.Width / 2;
                double y = origin.Y + rendered.Bounds.Center.Y - button.Bounds.Height / 2;

                if (Math.Abs(x) > 0.75 || Math.Abs(y) > 0.75)
                {
                    off.Add($"{ToolTip.GetTip(button) ?? button.Command?.GetType().Name} off by ({x:0.0}, {y:0.0}), icon {path.Bounds} align {path.HorizontalAlignment}/{path.VerticalAlignment} rendered {rendered.Bounds} origin {origin} in {button.Bounds.Width}x{button.Bounds.Height}");
                }
            }

            window.Close();
            return (off, count);
        });

        Assert.True(checkedIcons >= 3, $"only {checkedIcons} icon buttons were found");
        Assert.True(off.Count == 0, string.Join("\n", off));
    }

    [Fact]
    public async Task EnvironmentView_OffersTheManualUpdateCheckOnlyWithAnUpdateSystemAndTheUpdateButtonWhenOneIsAvailable()
    {
        connection.SetupGet(x => x.ServerVersion).Returns("1.0.0");
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment());
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<IReleaseFeed> feed = new();
        feed.Setup(x => x.Latest(It.IsAny<CancellationToken>())).ReturnsAsync(new ReleaseInfo { Version = new AppVersion { Major = 1, Minor = 4, Patch = 0 }, Assets = [] });

        (string[] without, string[] before, string[] after, bool dialog) = await fixture.Run(() =>
        {
            string[] Buttons(Window window) => [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty).Where(text => text is "Check for Updates" or "Update")];
            Window plain = new() { Width = 700, Height = 900, Content = new EnvironmentView { DataContext = new EnvironmentViewModel(connection.Object) } };
            plain.Show();
            Expand(plain);
            string[] without = Buttons(plain);
            plain.Close();

            using UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
            Window window = new() { Width = 700, Height = 900, Content = new EnvironmentView { DataContext = new EnvironmentViewModel(connection.Object, update) } };
            window.Show();
            Expand(window);
            string[] before = Buttons(window);
            update.CheckCommand.Execute(null);

            for (int attempt = 0; attempt < 200 && update.CheckResult is null or "Checking..."; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string[] after = Buttons(window);
            window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Content?.ToString() == "Update").Command!.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool dialog = window.GetVisualDescendants().OfType<UpdateDialogView>().Any(item => item.IsEffectivelyVisible);
            window.Close();
            return (without, before, after, dialog);
        });

        Assert.Empty(without);
        Assert.Equal(["Check for Updates"], before);
        Assert.Equal(["Check for Updates", "Update"], after);
        Assert.True(dialog);
    }

    [Fact]
    public async Task HomeView_SidebarHasTheGearAtTheTopFourMatchingTitleCaseButtonsBelowNoAddButtonsAndNoWorkspaceMoveButtons()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo inGroup = new() { Id = Guid.NewGuid(), Folder = "/work/a", GroupId = group.Id };
        WorkspaceInfo loose = new() { Id = Guid.NewGuid(), Folder = "/work/b" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [group], Workspaces = [inGroup, loose] });
        connection.SetupGet(x => x.ServerVersion).Returns("1.0.0");

        (bool gearAtTop, bool gearPlain, string[] bottom, bool bottomAlike, string[] groupButtons, string[] workspaceButtons, bool updateButton) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            model.SelectedEntry = model.Entries.First(entry => entry.Group == group);
            model.IsDrawerOpen = true;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            List<Button> buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)];
            Button gear = buttons.Single(button => ToolTip.GetTip(button)?.ToString() == "AI environment settings");
            Avalonia.Point gearAt = Avalonia.VisualExtensions.TranslatePoint(gear, new Avalonia.Point(0, 0), window)!.Value;
            bool gearAtTop = gearAt.Y < 60 && gearAt.X > 200 && gearAt.X < 340;
            bool gearPlain = gear.Classes.Contains("flat") && !gear.Classes.Contains("primary");
            Button[] bottom = [.. new[] { "New Workspace", "Clone Workspace", "New Group", "Change Server" }.Select(text => buttons.Single(button => button.Content?.ToString() == text))];
            bool bottomAlike = bottom.All(button => button.Classes.SequenceEqual(bottom[0].Classes) && button.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch) && !bottom.Any(button => button.Classes.Contains("primary"));
            DrawerEntry groupEntry = model.Entries.First(entry => entry.Group == group);
            string[] groupButtons = [.. window.GetVisualDescendants().OfType<Grid>().Where(grid => ReferenceEquals(grid.DataContext, groupEntry) && grid.IsEffectivelyVisible).SelectMany(grid => grid.GetVisualDescendants().OfType<Button>()).Select(button => button.Content?.ToString() ?? string.Empty)];
            DrawerEntry looseEntry = model.Entries.First(entry => entry.Workspace == loose);
            string[] workspaceButtons = [.. window.GetVisualDescendants().OfType<Grid>().Where(grid => ReferenceEquals(grid.DataContext, looseEntry) && grid.IsEffectivelyVisible).SelectMany(grid => grid.GetVisualDescendants().OfType<Button>()).Select(button => button.Content?.ToString() ?? string.Empty)];
            bool updateButton = buttons.Any(button => ToolTip.GetTip(button)?.ToString() == "Update available" && button.Bounds.Width > 0 && Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(0, 0), window)!.Value.X < 340);
            window.Close();
            return (gearAtTop, gearPlain, bottom.Select(button => button.Content!.ToString()!).ToArray(), bottomAlike, groupButtons, workspaceButtons, updateButton);
        });

        Assert.True(gearAtTop);
        Assert.True(gearPlain);
        Assert.Equal(["New Workspace", "Clone Workspace", "New Group", "Change Server"], bottom);
        Assert.True(bottomAlike);
        Assert.DoesNotContain("+", groupButtons);
        Assert.Equal(["▲", "▼", "✎", "↪", "✕"], groupButtons);
        Assert.Equal(["↪"], workspaceButtons);
        Assert.False(updateButton);
    }

    [Fact]
    public async Task HomeView_HighlightsWorkingWorkspacesAndKeepsTheFocusedOneSelectedAsItMovesToTheTop()
    {
        WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/a", LastUpdated = DateTimeOffset.UtcNow };
        WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/b", LastUpdated = DateTimeOffset.UtcNow.AddHours(-1) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [first, second] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        List<(string Rows, string? Selected, string Fill)> seen = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(second.Id).GetAwaiter().GetResult();
            model.IsDrawerOpen = true;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            List<(string, string?, string)> snapshots = [];

            void Look()
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ListBox list = view.FindControl<ListBox>("DrawerList")!;
                List<ListBoxItem> rows = [.. list.GetRealizedContainers().Cast<ListBoxItem>().OrderBy(row => row.Bounds.Y)];
                string text = string.Join(",", rows.Select(row => $"{((DrawerEntry)row.DataContext!).Text}{(row.Classes.Contains("working") ? "*" : string.Empty)}"));
                string fill = string.Join(",", rows.Select(row => row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter").Background is Avalonia.Media.ISolidColorBrush brush ? brush.Color.ToString() : "none"));
                snapshots.Add((text, (rows.SingleOrDefault(row => row.IsSelected)?.DataContext as DrawerEntry)?.Text, fill));
            }

            void State(WorkspaceInfo workspace, WorkspaceState state) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = state });

            Look();
            State(second, WorkspaceState.Working);
            Look();
            State(first, WorkspaceState.Managing);
            Look();
            State(second, WorkspaceState.Ready);
            Look();
            State(first, WorkspaceState.Ready);
            Look();
            window.Close();
            return snapshots;
        });

        Assert.Equal(["a,b", "b*,a", "a*,b*", "a*,b", "a,b"], seen.Select(snapshot => snapshot.Rows));
        Assert.All(seen, snapshot => Assert.Equal("b", snapshot.Selected));
        Assert.Equal("#ff007acc,none", seen[3].Fill.Replace("Transparent", "none").Replace("#ff094771", "none"));
    }

    [Fact]
    public async Task HomeView_KeepsTheScrollPositionAndTheSelectionWhileOtherWorkspacesStartAndStopWorking()
    {
        WorkspaceInfo[] many = [.. Enumerable.Range(10, 40).Select(number => new WorkspaceInfo { Id = Guid.NewGuid(), Folder = $"/work/w{number}" })];
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = many });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        List<(double Offset, string? Selected, string First)> seen = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(many[2].Id).GetAwaiter().GetResult();
            model.IsDrawerOpen = true;
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 600, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ListBox list = view.FindControl<ListBox>("DrawerList")!;
            ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Avalonia.Vector(0, 600);
            List<(double, string?, string)> snapshots = [];

            void Look()
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                snapshots.Add((scroller.Offset.Y, (list.SelectedItem as DrawerEntry)?.Text, model.Entries[0].Text));
            }

            void State(int index, WorkspaceState state) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = many[index].Id, State = state });

            Look();
            State(30, WorkspaceState.Working);
            Look();
            State(1, WorkspaceState.Working);
            Look();
            State(30, WorkspaceState.Ready);
            Look();
            State(1, WorkspaceState.Ready);
            Look();
            window.Close();
            return snapshots;
        });

        Assert.Equal(["w10", "w40", "w11", "w11", "w10"], seen.Select(snapshot => snapshot.First));
        Assert.All(seen, snapshot => Assert.Equal("w12", snapshot.Selected));
        Assert.All(seen, snapshot => Assert.Equal(seen[0].Offset, snapshot.Offset));
        Assert.True(seen[0].Offset > 0);
    }

    [Fact]
    public async Task HomeView_OpeningTheSidebarAfterTheFocusMovedShowsTheFocusedWorkspace()
    {
        WorkspaceInfo[] many = [.. Enumerable.Range(10, 40).Select(number => new WorkspaceInfo { Id = Guid.NewGuid(), Folder = $"/work/w{number}" })];
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = many });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        (double top, double height, double kept) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(many[0].Id).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 600, Content = view };
            window.Show();

            void Settle()
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            Settle();
            model.Selected = many[35];
            Settle();
            model.IsDrawerOpen = true;
            Settle();
            ListBox list = view.FindControl<ListBox>("DrawerList")!;
            Control row = list.ContainerFromItem(model.SelectedEntry!)!;
            double top = Avalonia.VisualExtensions.TranslatePoint(row, new Avalonia.Point(0, 0), list)!.Value.Y;
            ScrollViewer scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Avalonia.Vector(0, 0);
            model.IsDrawerOpen = false;
            Settle();
            model.IsDrawerOpen = true;
            Settle();
            (double, double, double) result = (top, list.Bounds.Height, scroller.Offset.Y);
            window.Close();
            return result;
        });

        Assert.InRange(top, 0, height - 20);
        Assert.Equal(0, kept);
    }

    [Fact]
    public async Task HomeView_HighlightsAClosedGroupOnlyWhileAWorkspaceInsideItIsWorking()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Team" };
        WorkspaceInfo inGroup = new() { Id = Guid.NewGuid(), Folder = "/work/beta", GroupId = group.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [group], Workspaces = [inGroup] });

        List<(bool Marked, string Fill, string Text)> seen = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(focusFirst: false).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 700, Content = view };
            window.Show();
            model.IsDrawerOpen = true;
            List<(bool, string, string)> snapshots = [];

            foreach (WorkspaceState state in new[] { WorkspaceState.Ready, WorkspaceState.Working, WorkspaceState.Ready })
            {
                connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = inGroup.Id, State = state });
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ListBoxItem row = (ListBoxItem)view.FindControl<ListBox>("DrawerList")!.ContainerFromIndex(0)!;
                Avalonia.Media.IBrush? fill = row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter").Background;
                TextBlock name = row.GetVisualDescendants().OfType<TextBlock>().First(block => block.IsEffectivelyVisible && block.Text == "▸ Team");
                snapshots.Add((row.Classes.Contains("working"), (fill as Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "none", (name.Foreground as Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "none"));
            }

            window.Close();
            return snapshots;
        });

        Assert.Equal([false, true, false], seen.Select(snapshot => snapshot.Marked));
        Assert.Equal("#ff007acc", seen[1].Fill);
        Assert.NotEqual("#ff007acc", seen[2].Fill);
        Assert.Equal(("White", seen[0].Text), (seen[1].Text, seen[2].Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeView_EveryTapOnAGroupTogglesItAndLeavesTheFocusedWorkspaceSelected(bool focused)
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Team" };
        WorkspaceInfo inGroup = new() { Id = Guid.NewGuid(), Folder = "/work/beta", GroupId = group.Id };
        WorkspaceInfo loose = new() { Id = Guid.NewGuid(), Folder = "/work/delta" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [group], Workspaces = [inGroup, loose] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });

        List<(string Rows, string? Selected)> seen = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start(focused ? loose.Id : null, focusFirst: false).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 700, Content = view };
            window.Show();
            model.IsDrawerOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ListBox list = view.FindControl<ListBox>("DrawerList")!;
            List<(string, string?)> snapshots = [];

            for (int tap = 0; tap < 3; tap++)
            {
                Control row = list.ContainerFromIndex(0)!;
                Avalonia.Point at = Avalonia.VisualExtensions.TranslatePoint(row, new Avalonia.Point(30, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, Avalonia.Input.MouseButton.Left);
                window.MouseUp(at, Avalonia.Input.MouseButton.Left);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                snapshots.Add((string.Join(",", list.GetRealizedContainers().Cast<ListBoxItem>().OrderBy(item => item.Bounds.Y).Select(item => ((DrawerEntry)item.DataContext!).Text)), (list.SelectedItem as DrawerEntry)?.Text));
            }

            window.Close();
            return snapshots;
        });

        Assert.Equal(["▾ Team,beta,delta", "▸ Team,delta", "▾ Team,beta,delta"], seen.Select(snapshot => snapshot.Rows));
        Assert.All(seen, snapshot => Assert.Equal(focused ? "delta" : null, snapshot.Selected));
    }

    [Fact]
    public async Task HomeView_TopRightIconButtonsAndTheWindowControlsAreTheSameSizeAndEvenlySpaced()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Ready);
        connection.Setup(x => x.ListFiles(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });

        (string[] tips, double[] widths, double[] gaps, double right) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1280, Height = 900, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Border topBar = window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("topbar"));
            string[] wanted = ["AI usage limits", "Workspace settings", "Minimize", "Maximize", "Restore", "Close"];
            List<Button> buttons = [.. topBar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && wanted.Contains(ToolTip.GetTip(button)?.ToString()))];
            buttons.Sort((left, right) => Avalonia.VisualExtensions.TranslatePoint(left, new Avalonia.Point(0, 0), window)!.Value.X.CompareTo(Avalonia.VisualExtensions.TranslatePoint(right, new Avalonia.Point(0, 0), window)!.Value.X));
            double[] lefts = [.. buttons.Select(button => Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(0, 0), window)!.Value.X)];
            double[] gaps = [.. lefts.Zip(buttons, (left, button) => left + button.Bounds.Width).Zip(lefts.Skip(1), (end, next) => Math.Round(next - end, 2))];
            double right = window.Width - (lefts[^1] + buttons[^1].Bounds.Width);
            window.Close();
            return (buttons.Select(button => ToolTip.GetTip(button)!.ToString()!).ToArray(), buttons.Select(button => Math.Round(button.Bounds.Width, 2)).ToArray(), gaps, right);
        });

        Assert.Equal(["AI usage limits", "Workspace settings", "Minimize", "Maximize", "Close"], tips);
        Assert.Single(widths.Distinct());
        Assert.All(gaps, gap => Assert.Equal(0, gap));
        Assert.InRange(right, 0, 16);
    }

    [Fact]
    public async Task HomeView_OffersDraftsWhileTheMessageBoxIsEmptyAndSaveDraftOnceSomethingIsTypedAndListsThemNewestFirst()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        SavedInput older = new() { Id = Guid.NewGuid(), Text = "older text", Time = DateTimeOffset.UtcNow.AddHours(-2) };
        SavedInput newer = new() { Id = Guid.NewGuid(), Text = "newer text", Time = DateTimeOffset.UtcNow.AddMinutes(-5) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([newer, older]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it" });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });

        (bool[] empty, bool[] typed, bool listed, string[] order, bool[] closed) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            TouchButton? Find(string tip) => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString()?.StartsWith(tip, StringComparison.Ordinal) == true);
            bool Panel() => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "Drafts");
            bool[] empty = [Find("Drafts") is not null, Find("Save Draft") is null, Panel()];

            model.Active!.Chat.ToggleSavedInputsCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool listed = Panel();
            string[] order = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.Text is "newer text" or "older text").Select(block => block.Text!)];

            model.Active.Chat.Input = "something";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] typed = [Find("Drafts") is null, Find("Save Draft") is not null, Find("Review the workspace") is null, Panel()];
            model.Active.Chat.Input = string.Empty;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] closed = [Find("Drafts") is not null, Panel()];
            window.Close();
            return (empty, typed, listed, order, closed);
        });

        Assert.Equal([true, true, false], empty);
        Assert.True(listed);
        Assert.Equal(["newer text", "older text"], order);
        Assert.Equal([true, true, true, false], typed);
        Assert.Equal([true, false], closed);
    }

    [Fact]
    public async Task HomeView_ShowsAScreenOfItsOwnWhileRecordingAndListsAnEntryBeingConvertedWithItsCancelButton()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        SavedInput converting = new() { Id = Guid.NewGuid(), Text = string.Empty, Time = DateTimeOffset.UtcNow, IsConverting = true };
        SavedInput failed = new() { Id = Guid.NewGuid(), Text = string.Empty, Time = DateTimeOffset.UtcNow.AddMinutes(-1), Error = "Nothing was heard." };
        SavedInput ready = new() { Id = Guid.NewGuid(), Text = "ready text", Time = DateTimeOffset.UtcNow.AddMinutes(-2) };
        Mock<IVoiceRecorder> recorder = new();
        recorder.SetupGet(x => x.SampleRate).Returns(16000);
        recorder.Setup(x => x.Start(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        recorder.Setup(x => x.Stop()).ReturnsAsync(new byte[16000 * 2]);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.TranscribeSavedInput(workspace.Id, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync([converting, failed, ready]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it" });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });

        (bool before, string[] recording, bool after, string[] listed) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Tabs, null, null, null, null, null, recorder.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 500, Height = 900, Content = view };
            window.Show();
            string[] Visible<T>(Func<T, string?> text) where T : Avalonia.Visual => [.. window.GetVisualDescendants().OfType<T>().Where(item => item.IsEffectivelyVisible).Select(text).Where(value => value is not null).Select(value => value!)];
            bool before = Visible<TextBlock>(block => block.Text).Contains("Listening...");

            model.Active!.Chat.DictateCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            string[] recording = [.. Visible<TextBlock>(block => block.Text).Where(text => text is "Listening..." or "0:00"), .. Visible<TouchButton>(button => button.Content as string).Where(text => text is "Complete" or "Cancel")];

            model.Active.Chat.CompleteDictationCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            bool after = Visible<TextBlock>(block => block.Text).Contains("Listening...");
            string[] listed = [.. Visible<TextBlock>(block => block.Text).Where(text => text is "Turning speech into text..." or "Nothing was heard." or "ready text"), .. Visible<TouchButton>(button => button.Classes.Contains("deleteSaved") ? "delete" : null)];
            window.Close();
            return (before, recording, after, listed);
        });

        Assert.False(before);
        Assert.Equal(["Listening...", "0:00", "Complete", "Cancel"], recording);
        Assert.False(after);
        // Every entry can be deleted, one still being converted too.
        Assert.Equal(["Turning speech into text...", "Nothing was heard.", "ready text", "delete", "delete", "delete"], listed);
    }

    [Fact]
    public async Task HomeView_ShowsALoopIconNextToTheNameOfEachWorkspaceWhereALoopRuns()
    {
        WorkspaceInfo quiet = new() { Id = Guid.NewGuid(), Folder = "/work/quiet", LastUpdated = DateTimeOffset.UtcNow };
        WorkspaceInfo busy = new() { Id = Guid.NewGuid(), Folder = "/work/busy", LastUpdated = DateTimeOffset.UtcNow.AddDays(-1) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [quiet, busy], Looping = [busy.Id] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });

        (string[] names, int icons) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            model.IsDrawerOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string[] names = [.. model.Entries.Select(entry => entry.Text)];
            int icons = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count(path => path.IsEffectivelyVisible && ToolTip.GetTip(path)?.ToString() == "A loop is running");
            window.Close();
            return (names, icons);
        });

        Assert.Equal(["busy", "quiet"], names);
        Assert.Equal(1, icons);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1200)]
    public async Task HomeView_OpensTheSessionsFromTheTitleAndShowsASideSessionAsOne(int width)
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/application", Service = AiService.Local };
        WorkspaceInfo side = new() { Id = Guid.NewGuid(), Folder = "/data/sessions/fix", Service = AiService.Local, Side = new SideSession { ParentId = workspace.Id, ParentName = "application", Name = "fix-login", Error = "Setting up the session failed: no" } };
        WorkspaceInfo integrating = new() { Id = Guid.NewGuid(), Folder = "/data/sessions/other", Service = AiService.Local, Side = new SideSession { ParentId = workspace.Id, ParentName = "application", Name = "other", State = SideSessionState.Integrating } };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace], Sessions = [side, integrating] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Service = AiService.Local });
        connection.Setup(x => x.GetGitStatus(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetGitChanges(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetUsage(AiService.Local, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.Local, Windows = [] });

        await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = width, Height = 900, Content = view };
            window.Show();

            void Settle()
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
            bool Shows(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == text);
            List<Button> Buttons(Avalonia.Visual within) => [.. within.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)];
            Button Labelled(Avalonia.Visual within, string text) => Buttons(within).Single(button => Equals(button.Content, text));

            Settle();
            Assert.False(Named<Border>("SideMarker").IsEffectivelyVisible);
            Assert.False(Named<TextBlock>("SessionContextLabel").IsEffectivelyVisible);
            Named<Button>("SessionsButton").Command!.Execute(null);
            Settle();
            Grid list = Named<Grid>("SessionList");
            Assert.True(list.IsEffectivelyVisible);
            Assert.True(Shows("Main"));
            Assert.True(Shows("fix-login"));
            Assert.True(Shows("Bringing In"));
            Assert.Single(Buttons(list), button => Equals(button.Content, "Cancel Bring In"));
            Button dots = Assert.Single(Buttons(list), button => Equals(button.Content, "⋯"));
            Assert.DoesNotContain(Buttons(list), button => Equals(button.Content, "Rename"));
            dots.Command!.Execute(dots.CommandParameter);
            Settle();

            foreach (string action in (string[])["Bring Into Main", "When Done: Bring Into Main", "Changes Since Session Start", "Rename", "Delete"])
            {
                Button button = Labelled(list, action);
                Avalonia.Point corner = Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(button.Bounds.Width, 0), window)!.Value;
                Assert.InRange(corner.X, button.Bounds.Width, width);
            }

            // Sessions are started from the message box, so the list only has the ones there are.
            Assert.DoesNotContain(Buttons(list), button => Equals(button.Content, "New Session"));

            model.Sessions.ChooseCommand.Execute(model.Sessions.Entries[1]);
            Settle();
            Assert.False(list.IsEffectivelyVisible);
            Assert.True(Named<Border>("SideMarker").IsEffectivelyVisible);
            TextBlock title = Named<TextBlock>("TitleText");
            Assert.Equal("application / fix-login", title.Text);
            Assert.True(title.Bounds.Width >= 100, $"the title has only {title.Bounds.Width} to show in");
            Button sessions = Named<Button>("SessionsButton");
            Assert.InRange(Avalonia.VisualExtensions.TranslatePoint(sessions, new Avalonia.Point(sessions.Bounds.Width, 0), window)!.Value.X, 0, width);
            Assert.True(Named<TextBlock>("SessionContextLabel").IsEffectivelyVisible);
            Assert.True(Shows("Has main's context from session creation."));
            Assert.True(Shows("Setting up the session failed: no"));

            model.Active!.ToggleSettingsCommand.Execute(null);
            Settle();
            string[] sections = [.. window.GetVisualDescendants().OfType<Expander>().Where(section => section.IsEffectivelyVisible).Select(section => section.Header?.ToString() ?? string.Empty)];
            Assert.DoesNotContain("Workspace", sections);
            Assert.DoesNotContain("Side sessions", sections);
            Assert.Contains("AI", sections);

            model.Active.Chat.Input = "do it";
            model.Active.OpenSendOptionsCommand.Execute(null);
            Settle();
            model.Active.SendOptions.IsNewSession = true;
            Settle();
            Grid panel = Named<Grid>("SendOptionsPanel");
            Assert.True(panel.IsEffectivelyVisible);
            Assert.Equal(["Current Commit:False", "Current Files:True"], panel.GetVisualDescendants().OfType<RadioButton>().Where(radio => radio.GroupName == "SessionStart").Select(radio => $"{radio.Content}:{radio.IsChecked}"));
            panel.GetVisualDescendants().OfType<RadioButton>().First(radio => radio.GroupName == "SessionStart").IsChecked = true;
            Settle();
            Assert.True(model.Active!.SendOptions.UseCurrentCommit);
            Assert.Equal("Start Session", Named<Button>("SendOptionsConfirm").Content);
            model.Active.SendOptions.CancelCommand.Execute(null);
            model.Sessions.CancelIntegrationCommand.Execute(model.Sessions.Entries[2]);
            Settle();
            Assert.Equal(("Cancel Bring In", "Keep Bringing In"), (Named<Button>("SessionConfirm").Content, Named<Button>("SessionDismiss").Content));
            window.Close();
            model.Dispose();
            return true;
        });
    }

    [Fact]
    public async Task HomeView_TakesKeysInTheSessionDialogsWithoutConfirmingWhatCannotBeUndone()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        WorkspaceInfo side = new() { Id = Guid.NewGuid(), Folder = "/data/sessions/fix", Side = new SideSession { ParentId = workspace.Id, ParentName = "app", Name = "fix-login" } };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace], Sessions = [side] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.RenameWorkspace(workspace.Id, "other", It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        await fixture.Dispatch(async () =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            await model.Start(workspace.Id);
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 900, Content = view };
            window.Show();

            async Task Settle()
            {
                await Task.Delay(50);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            async Task Press(Avalonia.Input.Key key, Avalonia.Input.PhysicalKey physical)
            {
                window.KeyPress(key, Avalonia.Input.RawInputModifiers.None, physical, null);
                await Settle();
            }

            await Settle();
            model.Sessions.ToggleCommand.Execute(null);
            await Settle();
            await Press(Avalonia.Input.Key.Escape, Avalonia.Input.PhysicalKey.Escape);
            Assert.False(model.Sessions.IsOpen);

            model.Sessions.ToggleCommand.Execute(null);
            model.Sessions.RenameCommand.Execute(model.Sessions.Entries[1]);
            await Settle();
            Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "SessionName").IsFocused);
            model.Sessions.Name = "better";
            await Press(Avalonia.Input.Key.Enter, Avalonia.Input.PhysicalKey.Enter);
            connection.Verify(x => x.RenameSideSession(side.Id, "better", It.IsAny<CancellationToken>()), Times.Once);

            model.Sessions.IntegrateCommand.Execute(model.Sessions.Entries[1]);
            await Settle();
            await Press(Avalonia.Input.Key.Enter, Avalonia.Input.PhysicalKey.Enter);
            Assert.Equal(SessionDialog.Integrate, model.Sessions.Dialog);
            connection.Verify(x => x.IntegrateSideSession(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            await Press(Avalonia.Input.Key.Escape, Avalonia.Input.PhysicalKey.Escape);
            Assert.Equal(SessionDialog.None, model.Sessions.Dialog);
            model.Sessions.IsOpen = false;

            model.BeginRenameCommand.Execute(null);
            await Settle();
            TextBox name = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsEffectivelyVisible && box.Text == "app");
            name.Focus();
            model.RenameText = "other";
            await Press(Avalonia.Input.Key.Enter, Avalonia.Input.PhysicalKey.Enter);
            connection.Verify(x => x.RenameWorkspace(workspace.Id, "other", It.IsAny<CancellationToken>()), Times.Once);
            window.Close();
            model.Dispose();
            return true;
        });
    }

    [Fact]
    public async Task HomeView_DrawsTheBranchOfASideSessionDashedWithItsName()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetGitStatus(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetGitChanges(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetGitHistory(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory
        {
            Commits = [new GitCommit { Hash = "abcdef123456", Parents = [], Author = "a", Date = DateTimeOffset.UtcNow, Subject = "first", Refs = [new GitRef { Name = "main", Kind = GitRefKind.Branch, IsCurrent = true }, new GitRef { Name = "fix-login", Kind = GitRefKind.Session }] }],
            HasMore = false
        });

        (int dashed, bool named) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Panels);
            model.Start(workspace.Id).GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1200, Height = 900, Content = view };
            window.Show();
            model.Active!.ShowHistoryCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            int dashed = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Count(outline => outline.Name == "SessionRefOutline" && outline.IsEffectivelyVisible && outline.StrokeDashArray is { Count: > 0 } && outline.Bounds.Width > 20);
            bool named = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "fix-login");
            window.Close();
            model.Dispose();
            return (dashed, named);
        });

        Assert.Equal((1, true), (dashed, named));
    }

    private WorkspaceInfo Repository()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        return workspace;
    }

    private SavedInput Queued(string text) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = true };

    private SavedInput Kept(string text) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, AutoCommit = false };

    private void Tell(Guid id, Agenda agenda) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.AgendaChanged, WorkspaceId = id, Agenda = agenda });

    [Theory]
    [InlineData(360, "", false)]
    [InlineData(360, "", true)]
    [InlineData(360, "some words", false)]
    [InlineData(360, "some words", true)]
    [InlineData(1280, "", true)]
    [InlineData(1280, "some words", true)]
    public async Task MessageBox_KeepsEveryButtonOnScreenWithoutOverlapWhateverItOffers(int width, string typed, bool working)
    {
        WorkspaceInfo workspace = Repository();
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([Queued("run next"), Kept("kept")]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working, AutoCommit = working ? AutoCommitState.On : AutoCommitState.Off });
        connection.Setup(x => x.GetLoop(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch });

        (string[] shown, string[] problems, double box, string above) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels, Mock.Of<IVoiceRecorder>());
            screen.Model.Active!.Chat.Input = typed;
            screen.Settle();
            AdaptiveRow row = screen.Named<AdaptiveRow>("ChatInputRow");
            string[] shown = [.. screen.Named<WrapPanel>("ChatButtons").Children.OfType<TouchButton>().Where(button => button.IsEffectivelyVisible).Select(button => button.Name ?? $"{ToolTip.GetTip(button)}".Split(':')[0].Split(' ')[0])];
            string[] problems = screen.Misplaced(row);
            double box = screen.Named<TextBox>("ChatInput").Bounds.Width;
            TouchButton commit = screen.Named<TouchButton>("AutoCommitButton");
            TextBlock note = screen.Named<TextBlock>("JoinNote");
            string above = $"{(commit.IsEffectivelyVisible ? screen.Named<TextBlock>("AutoCommitLabel").Text : "no commit button")}|{(note.IsEffectivelyVisible ? note.Text : "no note")}";
            problems = [.. problems, .. screen.Misplaced((Control)commit.Parent!)];
            return (shown, problems, box, above);
        });

        string[] expected = typed.Length > 0 ? ["CancelLoopButton", "SaveDraftButton", "SendButton"] : working ? ["DraftsButton", "UpNextButton", "CancelLoopButton", "ReviewButton", "ExpandButton", "Pause", "Stop"] : ["DraftsButton", "UpNextButton", "CancelLoopButton", "ReviewButton", "ExpandButton", "Resume"];
        Assert.Equal(expected, shown);
        Assert.Empty(problems);
        Assert.True(box >= 70, $"The message box is {box} wide.");
        Assert.Equal(!working ? "no commit button|no note" : typed.Length > 0 ? "When Done: Commit|Send joins the running work." : "When Done: Commit|no note", above);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1280)]
    public async Task StatusLine_ShowsTheStatusAndEveryButtonThatAppliesWithoutClippingAndOpensUpNext(int width)
    {
        WorkspaceInfo workspace = Repository();
        SavedInput queued = Queued("a rather long queued input that says in many words what the AI is to do after the work it does now");
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([queued]);
        connection.Setup(x => x.GetQueuedWork(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new QueueStatus { Position = 2, Text = "x" });
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], LimitPause = new LimitPause { Window = "5h", PercentUsed = 96, MaxPercent = 95, PausedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow } });

        (bool hidden, string[] buttons, string[] problems, string text, bool opened, string[] rows, int commitToggles, string[] panelProblems, bool asked) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            screen.Model.Active!.Chat.LimitPause?.ToString();
            Tell(workspace.Id, new Agenda
            {
                IsStopped = true,
                Status = "Stopped · 3 up next, and a status that is long enough to need more than one line on a narrow screen",
                Items =
                [
                    new AgendaItem { Kind = AgendaKind.Retry, Title = "Try the interrupted work again", Waiting = "usage limit reached", At = DateTimeOffset.UtcNow },
                    new AgendaItem { Kind = AgendaKind.BringIn, Id = Guid.NewGuid(), Title = "Bring in a-session-with-a-rather-long-name-of-its-own", AutoCommit = true },
                    new AgendaItem { Kind = AgendaKind.Queued, Id = queued.Id, Title = queued.Text, AutoCommit = false }
                ]
            });
            screen.Settle();
            Border line = screen.Named<Border>("StatusLine");
            string[] buttons = [.. screen.Named<WrapPanel>("StatusButtons").Children.OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => $"{button.Content}")];
            string[] problems = screen.Misplaced(line);
            string text = screen.Named<TextBlock>("StatusText").Text!;
            Button status = screen.Named<Button>("StatusButton");
            status.Command!.Execute(null);
            screen.Settle();
            Border panel = screen.Named<Border>("UpNextPanel");
            bool opened = panel.IsEffectivelyVisible;
            string[] rows = [.. panel.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button.Content is string).Select(button => (string)button.Content!)];
            int toggles = panel.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible && button.Classes.Contains("agendaCommit"));
            string[] panelProblems = screen.Misplaced(panel);
            Button delete = panel.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && Equals(button.Content, "Delete"));
            delete.Command!.Execute(delete.CommandParameter);
            screen.Settle();
            bool asked = screen.Named<Border>("DeleteSavedInputDialog").IsEffectivelyVisible;
            screen.Model.Active.Chat.CancelDeleteSavedInputCommand.Execute(null);
            screen.Model.Active.Chat.IsUpNextOpen = false;
            Tell(workspace.Id, new Agenda());
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspace.Id });
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspace.Id });
            screen.Settle();
            bool hidden = !line.IsEffectivelyVisible;
            return (hidden, buttons, problems, text, opened, rows, toggles, panelProblems, asked);
        });

        Assert.Equal(["Resume", "Cancel Auto Resume", "Cancel Waiting Work"], buttons);
        Assert.Empty(problems);
        Assert.StartsWith("Stopped · 3 up next", text);
        Assert.True(opened);
        Assert.Equal(["Close", "Edit", "Run in New Session", "Delete"], rows);
        Assert.Equal(2, commitToggles);
        Assert.Empty(panelProblems);
        Assert.True(asked);
        Assert.True(hidden);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1280)]
    public async Task SendOptions_ShowsTheChoicesOfEachModeWithoutClippingAndNamesWhatItsButtonDoes(int width)
    {
        WorkspaceInfo workspace = Repository();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review" });
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmStatus { Server = new SwarmServer { Name = "here" }, Peers = [new SwarmPeer { Id = "p", Name = "a-server-with-a-rather-long-name", Fingerprint = "f", Mode = SwarmMode.Direct, SendWork = true }] });

        (string[] confirms, string[] choices, string[] targets, string[] problems) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            screen.Model.Active!.Chat.Input = "do it";
            screen.Model.Active.OpenSendOptionsCommand.Execute(null);
            screen.Settle();
            SendOptionsViewModel options = screen.Model.Active.SendOptions;
            Grid panel = screen.Named<Grid>("SendOptionsPanel");
            Border card = screen.Named<Border>("SendOptionsCard");
            List<string> confirms = [];
            List<string> choices = [];
            List<string> problems = [];

            foreach (Action choose in new Action[] { () => options.IsNow = true, () => options.IsNext = true, () => options.IsRepeat = true, () => { options.IsNow = true; options.IsNewSession = true; }, () => options.Target = options.Targets[1] })
            {
                choose();
                screen.Settle();
                confirms.Add($"{screen.Named<Button>("SendOptionsConfirm").Content}");
                choices.Add(string.Join(',', panel.GetVisualDescendants().OfType<RadioButton>().Where(radio => radio.IsEffectivelyVisible && radio.GroupName is "SendDone" or "SessionDone" or "SessionStart").Select(radio => $"{radio.Content}{(radio.IsChecked == true ? "*" : string.Empty)}")));
                problems.AddRange(screen.Misplaced(card));
            }

            return (confirms.ToArray(), choices.ToArray(), options.Targets.Select(found => found.Name).ToArray(), problems.ToArray());
        });

        Assert.Equal(["Send", "Queue", "Start Loop", "Start Session", "Delegate Task"], confirms);
        Assert.Equal(["Commit,Keep Changes*", "Commit*,Keep Changes", "Commit,Keep Changes*", "Current Commit,Current Files*,Bring Into Main*,Keep Separate", "Bring Into Main*,Keep Separate"], choices);
        Assert.Equal(["This Server", "a-server-with-a-rather-long-name", "Any Server"], targets);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1280)]
    public async Task Drafts_OfferLoadingCommittingRunningInANewSessionAndDeletingForEachDraftOnly(int width)
    {
        Repository();
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([Queued("queued one"), Kept("kept"), new SavedInput { Id = Guid.NewGuid(), Text = "also kept", Time = DateTimeOffset.UtcNow }]);

        (string[] rows, bool[] commits, int runs, string[] problems) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            screen.Model.Active!.Chat.ToggleSavedInputsCommand.Execute(null);
            screen.Settle();
            ItemsControl list = screen.Named<ItemsControl>("SavedInputList");
            string[] rows = [.. list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("savedRow")).Select(border => $"{string.Join('/', border.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.Text is "queued one" or "kept" or "also kept").Select(block => block.Text))} {border.GetVisualDescendants().OfType<TouchButton>().Count(button => button.Classes.Contains("deleteSaved"))}")];
            // Each entry says for itself whether it is committed after.
            bool[] commits = [.. list.GetVisualDescendants().OfType<TouchButton>().Where(button => button.Classes.Contains("savedCommit") && button.IsEffectivelyVisible).Select(button => button.Classes.Contains("toggled"))];
            int runs = list.GetVisualDescendants().OfType<TouchButton>().Count(button => button.Classes.Contains("runSaved") && button.IsEffectivelyVisible && Equals(ToolTip.GetTip(button), "Run in New Session"));
            return (rows, commits, runs, screen.Misplaced(screen.Named<Border>("DraftsPanel")));
        });

        Assert.Equal(["kept 1", "also kept 1"], rows);
        Assert.Equal([false, true], commits);
        Assert.Equal(2, runs);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1280)]
    public async Task Activity_HasItsOwnTabWithEntriesThatExpandAndALoadMoreButton(int width)
    {
        WorkspaceInfo workspace = Repository();
        ActivityEntry entry = new() { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, Source = ActivitySource.Queue, Title = "A queued input with a title long enough to wrap onto a second line on a narrow screen", Text = "the input", Outcome = ActivityOutcome.Failed, Reason = "it broke", Report = "I tried.", CommitError = "no identity", Handoff = "from the session", Files = ["a/very/long/path/to/a/file/that/was/changed/by/the/session/that/was/brought/in.cs"], Notes = ["Tried again"], NeedsAttention = true };
        connection.Setup(x => x.GetActivity(workspace.Id, null, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActivityPage { Entries = [entry], Older = "older" });

        (string[] tabs, string[] closed, string[] open, bool more, string[] problems) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            string[] tabs = [.. screen.Window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible).Select(button => $"{button.Content}")];
            screen.Model.Active!.ShowActivityCommand.Execute(null);
            screen.Settle();
            ScrollViewer view = screen.Named<ScrollViewer>("ActivityView");
            string[] Texts() => [.. view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text)).Select(block => block.Text!)];
            string[] closed = Texts();
            List<string> problems = [.. screen.Misplaced(view)];
            Button header = view.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("activity"));
            header.Command!.Execute(header.CommandParameter);
            screen.Settle();
            string[] open = Texts();
            problems.AddRange(screen.Misplaced(view));
            bool more = view.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && Equals(button.Content, "Load More"));
            return (tabs, closed, open, more, problems.ToArray());
        });

        Assert.Equal(width < 600 ? ["Chat", "Files", "Script", "Activity", "History"] : ["Chat", "Script", "Activity", "History"], tabs);
        Assert.Contains("Failed", closed);
        Assert.Contains("Needs Attention", closed);
        Assert.DoesNotContain("the input", closed);
        Assert.All(new[] { "the input", "it broke", "I tried.", "Commit failed: no identity", "from the session", "Tried again" }, text => Assert.Contains(text, open));
        Assert.True(more);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(1280)]
    public async Task Sessions_ListDelegatedTasksWhoseDetailShowsWithItsActionsWithoutClipping(int width)
    {
        WorkspaceInfo workspace = Repository();
        SwarmTaskInfo task = new() { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Title = "Port the parser to the new interface everywhere", State = SwarmTaskState.Done, PeerName = "a-server-with-a-rather-long-name", Branch = "swarm/x", BringIn = true };
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmStatus { Server = new SwarmServer { Name = "here" }, Tasks = [task] });
        connection.Setup(x => x.GetSwarmTask(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmTaskDetail { Task = task, Instructions = "do it", Report = "done it", Messages = [new ChatMessage { Role = MessageRole.Assistant, Text = "working on it" }] });

        (string row, string[] texts, string[] actions, string[] problems, bool closed) = await fixture.Run(() =>
        {
            using Screen screen = new(connection.Object, width, width < 600 ? ClientLayout.Tabs : ClientLayout.Panels);
            screen.Model.Sessions.ToggleCommand.Execute(null);
            screen.Settle();
            ItemsControl list = screen.Named<ItemsControl>("DelegatedTaskList");
            string row = list.GetVisualDescendants().OfType<TextBlock>().Single().Text!;
            List<string> problems = [.. screen.Misplaced(screen.Named<Border>("SessionCard"))];
            screen.Model.Sessions.OpenTaskCommand.Execute(screen.Model.Sessions.Tasks[0]);
            screen.Settle();
            Border detail = screen.Named<Border>("TaskDetailOverlay");
            string[] texts = [.. detail.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text)).Select(block => block.Text!)];
            string[] actions = [.. screen.Named<WrapPanel>("TaskActions").Children.OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => $"{button.Content}")];
            problems.AddRange(screen.Misplaced(detail));
            screen.Window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            screen.Settle();
            return (row, texts, actions, problems.ToArray(), !detail.IsEffectivelyVisible);
        });

        Assert.Equal("Port the parser to the new interface everywhere · a-server-with-a-rather-long-name · Done", row);
        Assert.All(new[] { "do it", "done it", "working on it" }, text => Assert.Contains(text, texts));
        Assert.Equal(["Bring Into Main", "Continue Here", "When Done: Bring Into Main", "Discard Task"], actions);
        Assert.Empty(problems);
        Assert.True(closed);
    }

    [Fact]
    public async Task NotifyButton_ShowsBesideTheTitleOfAWorkingWorkspaceAndFillsItsBellOnceOn()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });

        (bool whenReady, bool whenWorking, bool onBefore, bool onAfter, bool filled, double width, double right, double title) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object, notifier: Mock.Of<IReadyNotifier>());
            model.Start().GetAwaiter().GetResult();
            Window window = new() { Width = 360, Height = 800, Content = new HomeView { DataContext = model } };
            window.Show();
            model.IsDrawerOpen = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TouchButton button = window.GetVisualDescendants().OfType<TouchButton>().Single(found => found.Name == "NotifyButton");
            bool whenReady = button.IsEffectivelyVisible;
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool whenWorking = button.IsEffectivelyVisible;
            bool onBefore = button.Classes.Contains("on");
            button.Command!.Execute(button.CommandParameter);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Controls.Shapes.Path bell = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            bool onAfter = button.Classes.Contains("on");
            bool filled = bell.Fill is Avalonia.Media.ISolidColorBrush { Color.A: > 0 };
            double width = button.Bounds.Width;
            double right = Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(button.Bounds.Width, 0), window)!.Value.X;
            double title = window.GetVisualDescendants().OfType<TextBlock>().Single(found => found.Name == "TitleText").Bounds.Width;
            window.Close();
            model.Dispose();
            return (whenReady, whenWorking, onBefore, onAfter, filled, width, right, title);
        });

        Assert.False(whenReady);
        Assert.True(whenWorking);
        Assert.False(onBefore);
        Assert.True(onAfter);
        Assert.True(filled);
        // On a narrow screen the whole button stays left of the two buttons at the right, beside a title that can still be read.
        Assert.Equal(34, width);
        Assert.InRange(right, 200, 360 - 76 - 8);
        Assert.True(title > 30, $"The title is {title} wide.");
    }

    [Fact]
    public async Task Resources_ShowInTheWorkspaceListAndInTheSectionOfBothSettingsScreens()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        ResourceUsage taken = new() { Memory = 300 * 1024 * 1024, Cpu = 12, Processes = 1, Heaviest = [new ProcessUsage { Id = 5, Command = "dotnet build", Memory = 300 * 1024 * 1024, Cpu = 12 }] };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace], Busy = [workspace.Id] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment());
        connection.Setup(x => x.GetResources(It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceReport { Supported = true, Workspaces = [new WorkspaceResources { Id = workspace.Id, Name = "app", IsWorking = true, Total = taken, Sessions = [new SessionResources { Id = workspace.Id, Name = "Main", IsWorking = true, Usage = taken }] }] });

        (string[] listed, string[] ofWorkspace, string[] ofAll) = await fixture.Run(() =>
        {
            string[] Shown(Window window)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? string.Empty)];
            }

            string[] Expanded(Window window)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.GetVisualDescendants().OfType<Expander>().Single(section => section.Header?.ToString() == "Resources").IsExpanded = true;
                return Shown(window);
            }

            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1600, Height = 1000, Content = view };
            window.Show();
            model.IsDrawerOpen = true;
            string[] listed = Shown(window);
            model.IsDrawerOpen = false;
            model.Active!.ToggleSettingsCommand.Execute(null);
            string[] ofWorkspace = Expanded(window);
            window.Close();
            model.Dispose();

            EnvironmentViewModel environment = new(connection.Object);
            environment.Resources.IsOnScreen = true;
            environment.Load().GetAwaiter().GetResult();
            Window global = new() { Width = 1600, Height = 1000, Content = new EnvironmentView { DataContext = environment } };
            global.Show();
            string[] ofAll = Expanded(global);
            global.Close();
            environment.Resources.IsOnScreen = false;
            return (listed, ofWorkspace, ofAll);
        });

        Assert.Contains("300 MB · 12% CPU", listed);
        Assert.Contains("Main", ofWorkspace);
        Assert.Contains("300 MB · 12% CPU · 1 process", ofWorkspace);
        Assert.Contains("dotnet build", ofWorkspace);
        Assert.Contains("app", ofAll);
        Assert.Contains("Server itself", ofAll);
        Assert.Contains("dotnet build", ofAll);
    }

    [Fact]
    public async Task Settings_AreSortedIntoSectionsThatStartCollapsedInAColumnOfLimitedWidth()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Descriptors = new Dictionary<string, string> { ["Model"] = "x" } });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [] });
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment());

        (string[] workspaceSections, bool[] workspaceOpen, double workspaceWidth, string[] globalSections, bool[] globalOpen, double globalWidth, bool opens) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 1600, Height = 1000, Content = view };
            window.Show();
            model.Active!.ToggleSettingsCommand.Execute(null);

            for (int attempt = 0; attempt < 100 && model.Active.Settings.Details.Count == 0; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            List<Expander> sections = [.. window.GetVisualDescendants().OfType<Expander>().Where(section => section.IsEffectivelyVisible)];
            string[] workspaceSections = [.. sections.Select(section => section.Header?.ToString() ?? string.Empty)];
            bool[] workspaceOpen = [.. sections.Select(section => section.IsExpanded)];
            double workspaceWidth = sections[0].Bounds.Width;
            sections[0].IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool opens = window.GetVisualDescendants().OfType<ComboBox>().Any(box => box.IsEffectivelyVisible);
            window.Close();

            EnvironmentViewModel environment = new(connection.Object);
            environment.Load().GetAwaiter().GetResult();
            Window global = new() { Width = 1600, Height = 1000, Content = new EnvironmentView { DataContext = environment } };
            global.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            List<Expander> globals = [.. global.GetVisualDescendants().OfType<Expander>().Where(section => section.IsEffectivelyVisible)];
            string[] globalSections = [.. globals.Select(section => section.Header?.ToString() ?? string.Empty)];
            bool[] globalOpen = [.. globals.Select(section => section.IsExpanded)];
            double globalWidth = globals[0].Bounds.Width;
            global.Close();
            return (workspaceSections, workspaceOpen, workspaceWidth, globalSections, globalOpen, globalWidth, opens);
        });

        Assert.Equal(["AI", "Access", "Instructions and prompts", "Side sessions", "Resources", "How it runs", "Errors", "Workspace"], workspaceSections);
        Assert.All(workspaceOpen, open => Assert.False(open));
        Assert.Equal(["Resources", "New workspaces", "Status and prompts", "Claude Code", "OpenAI Codex", "Local models", "Image generation"], globalSections);
        Assert.All(globalOpen, open => Assert.False(open));
        Assert.InRange(workspaceWidth, 600, 640);
        Assert.InRange(globalWidth, 600, 640);
        Assert.True(opens);
    }

    private sealed class Screen : IDisposable
    {
        public Screen(IServerConnection connection, int width, ClientLayout layout = ClientLayout.Panels, IVoiceRecorder? recorder = null)
        {
            Model = new(connection, layout, null, null, null, null, null, recorder);
            Model.Start().GetAwaiter().GetResult();
            Window = new() { Width = width, Height = 800, Content = new HomeView { DataContext = Model } };
            Window.Show();
            Model.IsDrawerOpen = false;
            Settle();
        }

        public HomeViewModel Model { get; }

        public Window Window { get; }

        public void Settle()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            }
        }

        public T Named<T>(string name) where T : Control => Window.GetVisualDescendants().OfType<T>().Single(found => found.Name == name);

        // What can be seen of the controls under a root that reaches past it, is squeezed or lies over another.
        public string[] Misplaced(Control root)
        {
            List<string> problems = [];
            List<(Control Control, Avalonia.Rect Area)> buttons = [];

            foreach (Control control in root.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control is Button or TextBlock or TextBox or ComboBox or NumericUpDown && !control.GetVisualAncestors().TakeWhile(ancestor => ancestor != root).Any(ancestor => ancestor is Button or TextBox or ComboBox or NumericUpDown or TimePicker or Avalonia.Controls.Primitives.ScrollBar)))
            {
                Avalonia.Point corner = Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(), root)!.Value;
                Avalonia.Rect area = new(corner, control.Bounds.Size);

                // What is scrolled out of view lies over nothing.
                foreach (ScrollViewer scroller in control.GetVisualAncestors().OfType<ScrollViewer>())
                {
                    Avalonia.Rect view = new(Avalonia.VisualExtensions.TranslatePoint(scroller, new Avalonia.Point(), root)!.Value, scroller.Bounds.Size);
                    area = new Avalonia.Rect(area.Left, Math.Max(area.Top, view.Top), area.Width, Math.Max(0, Math.Min(area.Bottom, view.Bottom) - Math.Max(area.Top, view.Top)));
                }

                if (area.Height < 1)
                {
                    continue;
                }

                string name = control.Name ?? (control as Button)?.Content as string ?? (control as TextBlock)?.Text ?? control.GetType().Name;

                if (area.Left < -0.5 || area.Right > root.Bounds.Width + 0.5)
                {
                    problems.Add($"{name} reaches from {area.Left:0} to {area.Right:0} of {root.Bounds.Width:0}");
                }

                if (control is Button button)
                {
                    if (button.Content is string text && button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() is { } written && (written.Bounds.Width > button.Bounds.Width + 0.5 || written.DesiredSize.Width > written.Bounds.Width + written.Margin.Left + written.Margin.Right + 1))
                    {
                        problems.Add($"{text} does not fit its button of {button.Bounds.Width:0}: it takes {written.DesiredSize.Width:0} and has {written.Bounds.Width:0}");
                    }

                    if (area.Width < 1 || area.Height < 1)
                    {
                        problems.Add($"{name} has no size");
                    }

                    foreach ((Control other, Avalonia.Rect taken) in buttons)
                    {
                        Avalonia.Rect shared = taken.Intersect(area);

                        if (shared.Width > 1 && shared.Height > 1)
                        {
                            problems.Add($"{name} lies over {other.Name ?? (other as Button)?.Content}");
                        }
                    }

                    buttons.Add((button, area));
                }
            }

            return [.. problems];
        }

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
        }
    }
}
