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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }, new ChatMessage { Role = MessageRole.Assistant, Text = "hello" }] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working });

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
    public async Task HomeView_SettingsShowTheLockedProviderAndOneControlForEachSetting()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Codex };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Service = AiService.Codex, Capability = AiCapability.Strong, Effort = AiEffort.Low, IsShellAllowed = true, Instructions = "be brief", Descriptors = new Dictionary<string, string> { ["Model"] = "GPT 5.6" } });

        (int choiceBoxes, int toggles, int numbers, bool providerShown, bool instructionsShown, bool detailShown, string capability) = await fixture.Run(() =>
        {
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            model.Active!.ToggleSettingsCommand.Execute(null);

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
            window.Close();
            return (choiceBoxes, toggles, numbers, providerShown, instructionsShown, detailShown, capability);
        });

        Assert.Equal(2, choiceBoxes);
        Assert.Equal(4, toggles);
        Assert.Equal(1, numbers);
        Assert.True(providerShown);
        Assert.True(instructionsShown);
        Assert.True(detailShown);
        Assert.Equal("Strong", capability);
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
            int Refresh() => window.GetVisualDescendants().OfType<Button>().Count(button => button.IsEffectivelyVisible && button.Content?.ToString() is "Refresh Models" or "Refresh Checkpoints");
            int refreshButtons = Refresh();
            model.SelectedImage = model.ImageKinds.Single(choice => choice.Value == ImageGenerationKind.Local);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            int refreshWithLocal = Refresh();
            int rowBoxes = window.GetVisualDescendants().OfType<ComboBox>().Count(box => box.IsEditable && box.IsEffectivelyVisible);
            int clearBoxes = window.GetVisualDescendants().OfType<CheckBox>().Count(box => box.IsEffectivelyVisible);
            int switchesAndDefaults = window.GetVisualDescendants().OfType<ToggleSwitch>().Count(item => item.IsEffectivelyVisible);
            bool defaultsShown = window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "New workspaces") && window.GetVisualDescendants().OfType<TextBox>().Any(box => box.IsEffectivelyVisible && box.PlaceholderText == "Standing instructions for the AI...");
            window.Close();
            return (refreshButtons, refreshWithLocal, rowBoxes, clearBoxes, switchesAndDefaults, defaultsShown);
        });

        Assert.Equal(1, refreshButtons);
        Assert.Equal(2, refreshWithLocal);
        Assert.Equal(4, switchesAndDefaults);
        Assert.True(defaultsShown);
        Assert.Equal(8, rowBoxes);
        Assert.Equal(1, clearBoxes);
    }

    [Fact]
    public async Task HomeView_ShowsTheLocalTimeUnderEachMessageThatHasOne()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        DateTimeOffset written = DateTimeOffset.UtcNow.AddMinutes(-3);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
    public async Task HomeView_ShowsALoopButtonNextToReviewOpensThePanelAndShowsTheLoopInTheHeaderWithAButtonToCancelIt()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it" });
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "Session", PercentUsed = 10, WindowMinutes = 300 }] });

        (bool[] idle, bool panelOpen, string[] conditions, bool[] fields, bool[] looping, bool tipHasRuns, bool[] working) = await fixture.Run(() =>
        {
            bool[] working;
            HomeViewModel model = new(connection.Object);
            model.Start().GetAwaiter().GetResult();
            HomeView view = new() { DataContext = model };
            Window window = new() { Width = 900, Height = 900, Content = view };
            window.Show();
            TouchButton? Find(string tip) => window.GetVisualDescendants().OfType<TouchButton>().FirstOrDefault(button => button.IsEffectivelyVisible && ToolTip.GetTip(button)?.ToString()?.StartsWith(tip, StringComparison.Ordinal) == true);
            bool Indicator() => window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(path => path.Name == "LoopIndicator" && path.IsEffectivelyVisible);
            double Left(string tip) => Avalonia.VisualExtensions.TranslatePoint(Find(tip)!, new Avalonia.Point(), window)!.Value.X;
            bool[] idle = [Find("Review the workspace") is not null && Find("Resume: ask") is not null && Find("Pause: ask") is null, Find("Loop: send") is { } plain && plain.Classes.Contains("action") && !plain.Classes.Contains("primary") && plain.Width == 34 && plain.Height == 34, Find("Cancel the loop") is not null, Indicator(), Left("Loop: send") < Left("Review the workspace") && Left("Review the workspace") < Left("Resume: ask")];

            model.Active!.OpenLoopCommand.Execute(null);
            for (int attempt = 0; attempt < 100 && !model.Active.Loop.IsOpen; attempt++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string[] conditions = [.. model.Active.Loop.Conditions.Select(choice => choice.Name)];
            bool Shown<T>() where T : Avalonia.Visual => window.GetVisualDescendants().OfType<T>().Any(item => item.IsEffectivelyVisible);
            bool radios = window.GetVisualDescendants().OfType<RadioButton>().Count(radio => radio.IsEffectivelyVisible) == 3;
            model.Active.Loop.SelectedCondition = model.Active.Loop.Conditions.Single(choice => choice.Condition == LoopCondition.UntilTime);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool timePicker = Shown<TimePicker>();
            model.Active.Loop.SelectedCondition = model.Active.Loop.Conditions.Single(choice => choice.Condition == LoopCondition.UntilWindow);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool windowPicker = window.GetVisualDescendants().OfType<ComboBox>().Count(box => box.IsEffectivelyVisible && box.ItemsSource is IReadOnlyList<string>) > 0;
            bool[] fields = [radios, timePicker, windowPicker];
            model.Active.Loop.CancelCommand.Execute(null);

            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", IntervalSeconds = 600, Condition = LoopCondition.Runs, Runs = 4 }, StartedAt = DateTimeOffset.UtcNow, Runs = 2 } });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] looping = [Find("Loop: send") is not null, Find("Cancel the loop") is not null, Indicator(), Find("Review the workspace") is not null];
            bool tipHasRuns = model.Active.LoopTip.Contains("2 runs so far", StringComparison.Ordinal);
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id });
            connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            working = [Find("Loop: send") is not null, Find("Stop the work") is not null, Find("Review the workspace") is not null, Find("Pause: ask") is { } pause && pause.Classes.Contains("primary"), Find("Resume: ask") is not null, Left("Loop: send") < Left("Pause: ask")];
            window.Close();
            return (idle, model.Active.Loop.IsOpen, conditions, fields, looping, tipHasRuns, working);
        });

        Assert.Equal([true, true, false, false, true], idle);
        Assert.False(panelOpen);
        Assert.Equal(5, conditions.Length);
        Assert.Equal([true, true, true], fields);
        Assert.Equal([false, true, true, true], looping);
        Assert.True(tipHasRuns);
        Assert.Equal([true, true, true, true, false, true], working);
    }

    [Fact]
    public async Task HomeView_ShowsAnEqualHistoryTabOnlyWhenTheWorkspaceIsAGitRepository()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
    public async Task ScrollSafeTextBlock_ATouchAnywhereDropsTheSelectionAHoldMade()
    {
        (string held, string after) = await fixture.Run(() =>
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
            second.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(second, new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Touch, true), window, new Avalonia.Point(5, 25), 0, properties, Avalonia.Input.KeyModifiers.None, 1) { RoutedEvent = Avalonia.Input.InputElement.PointerPressedEvent });
            string after = first.SelectedText;
            window.Close();
            return (held, after);
        });

        Assert.Equal("some", held);
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
            string[] Shown() => [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Command == model.Active?.Chat.SendCommand ? "send" : button.Command == model.Active?.Chat.ReviewCommand ? "review" : button.Command == model.BeginStopCommand ? "stop" : string.Empty).Where(name => name.Length > 0)];
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working });
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
            string[] without = Buttons(plain);
            plain.Close();

            using UpdateViewModel update = new(info.Object, connection.Object, feed.Object, null);
            Window window = new() { Width = 700, Height = 900, Content = new EnvironmentView { DataContext = new EnvironmentViewModel(connection.Object, update) } };
            window.Show();
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
    public async Task HomeView_TopRightIconButtonsAndTheWindowControlsAreTheSameSizeAndEvenlySpaced()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
    public async Task HomeView_OffersLoadingSavedInputsWhileTheMessageBoxIsEmptyAndSavingOnceSomethingIsTypedAndListsTheEntriesNewestFirst()
    {
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        SavedInput older = new() { Id = Guid.NewGuid(), Text = "older text", Time = DateTimeOffset.UtcNow.AddHours(-2) };
        SavedInput newer = new() { Id = Guid.NewGuid(), Text = "newer text", Time = DateTimeOffset.UtcNow.AddMinutes(-5) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([newer, older]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
            bool Panel() => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == "Saved inputs");
            bool[] empty = [Find("Load a saved input") is not null, Find("Save the input") is null, Panel()];

            model.Active!.Chat.ToggleSavedInputsCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool listed = Panel();
            string[] order = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.Text is "newer text" or "older text").Select(block => block.Text!)];

            model.Active.Chat.Input = "something";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] typed = [Find("Load a saved input") is null, Find("Save the input") is not null, Find("Review the workspace") is null, Panel()];
            model.Active.Chat.Input = string.Empty;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            bool[] closed = [Find("Load a saved input") is not null, Panel()];
            window.Close();
            return (empty, typed, listed, order, closed);
        });

        Assert.Equal([true, true, false], empty);
        Assert.True(listed);
        Assert.Equal(["newer text", "older text"], order);
        Assert.Equal([true, true, true, false], typed);
        Assert.Equal([true, false], closed);
    }
}
