namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Home screen.
/// </summary>
public sealed partial class HomeView : UserControl
{
    private static FileNode? NodeOf(object? source) => (source as StyledElement)?.DataContext as FileNode;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeView" /> class.
    /// </summary>
    public HomeView()
    {
        InitializeComponent();
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnFilePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnFileHoldStart, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, OnFileHoldMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnFileHoldEnd, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        this.FindControl<ScrollViewer>("MessageScroller")?.AddHandler(Avalonia.Input.InputElement.PointerWheelChangedEvent, OnMessagesWheel, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        this.FindControl<TextBox>("ChatInput")?.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnChatKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnPromptKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        if (this.FindControl<Border>("PromptOverlay") is { } overlay && this.FindControl<TextBox>("PromptInput") is { } prompt)
        {
            overlay.PropertyChanged += (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty && overlay.IsVisible)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (prompt.IsEffectivelyVisible)
                        {
                            prompt.Focus();
                            prompt.SelectAll();
                        }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }

        if (this.FindControl<Border>("SessionDialogOverlay") is { } asking && this.FindControl<TextBox>("SessionName") is { } named && this.FindControl<Border>("SessionDialogCard") is { } dialogCard)
        {
            // The dialog takes the keyboard: its name, or where there is none itself, so that Enter confirms nothing.
            asking.PropertyChanged += (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty && asking.IsVisible)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (named.IsEffectivelyVisible)
                        {
                            named.Focus();
                            named.SelectAll();
                        }
                        else
                        {
                            dialogCard.Focus();
                        }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
                else if (args.Property == Visual.IsVisibleProperty)
                {
                    // Closing it leaves the keyboard nowhere again, and Escape has to reach the list it was opened from.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (this.FindControl<Border>("SessionCard") is { IsEffectivelyVisible: true } list)
                        {
                            list.Focus();
                        }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }

        if (this.FindControl<Grid>("SendOptionsPanel") is { } sendOptions && this.FindControl<Border>("SendOptionsCard") is { } optionsCard && this.FindControl<TextBox>("ChatInput") is { } messageBox)
        {
            bool typing = false;
            // It takes the keyboard, so that Enter and Escape are about it and nothing is typed behind it. The message box gets it back if it had it.
            sendOptions.PropertyChanged += (_, args) =>
            {
                if (args.Property != Visual.IsVisibleProperty)
                {
                    return;
                }

                if (sendOptions.IsVisible)
                {
                    typing = messageBox.IsFocused;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => optionsCard.Focus(), Avalonia.Threading.DispatcherPriority.Background);
                }
                else if (typing)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => messageBox.Focus(), Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }

        if (this.FindControl<Border>("TaskDetailOverlay") is { } taskDetail)
        {
            // It takes the keyboard, so that Escape closes it.
            taskDetail.PropertyChanged += (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty && taskDetail.IsVisible)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => taskDetail.Focus(), Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }

        if (this.FindControl<Grid>("SessionList") is { } sessionList && this.FindControl<Border>("SessionCard") is { } card)
        {
            // Opening it leaves the keyboard nowhere, since the title is held back while it is clicked, and Escape has to reach it.
            sessionList.PropertyChanged += (_, args) =>
            {
                if (args.Property == Visual.IsVisibleProperty && sessionList.IsVisible)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => card.Focus(), Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }

        if (this.FindControl<ListBox>("DrawerList") is { } drawer)
        {
            // The highlight belongs to the whole row, which only the item's container covers; a row that changes is a new item, so preparing it is enough.
            drawer.ContainerPrepared += (_, args) => args.Container.Classes.Set("working", drawer.Items[args.Index] is DrawerEntry { IsWorking: true });
        }

        if (this.FindControl<TextBox>("ChatInput") is { } input)
        {
            input.TextChanged += (_, _) => OnChatTextChanged();
        }

        if (this.FindControl<Grid>("ChatGrid") is { } grid)
        {
            grid.RowDefinitions[2].PropertyChanged += (_, args) =>
            {
                if (!fitting && args.Property == RowDefinition.HeightProperty)
                {
                    restoreHeight = null;
                }
            };
        }
    }

    private readonly double holdSlop = 10;

    private CancellationTokenSource? fileHold;

    private Avalonia.Point holdStart;

    private bool held;


    private bool loadingOlder;

    private Control? anchor;

    private double anchorContent;

    private int anchorIndex;

    private readonly List<(ChatMessage Message, double Top)> onScreen = [];

    private List<(ChatMessage Message, double Top)>? returning;

    private int returnAttempts;

    private bool laidOutHooked;

    private bool fitting;

    private ChatViewModel? followed;

    private double? restoreHeight;

    private HomeViewModel? home;

    private bool revealFocused;

    /// <summary>
    /// Gets or sets how long a finger has to stay on a script or task file of the file system on mobile before it is run.
    /// </summary>
    internal TimeSpan FileHoldDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private ChatViewModel? Chat => (DataContext as HomeViewModel)?.Active?.Chat;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs args)
    {
        base.OnDataContextChanged(args);

        if (home is not null)
        {
            home.PropertyChanged -= OnHomeChanged;
        }

        home = DataContext as HomeViewModel;
        revealFocused = home is not null;

        if (home is not null)
        {
            home.AfterSelection = action => Dispatcher.UIThread.Post(action);
            home.PropertyChanged += OnHomeChanged;
            RevealFocused();
        }

        if (DataContext is HomeViewModel { IsDesktop: true } && this.FindControl<Grid>("ChatGrid") is { } grid && this.FindControl<TextBox>("ChatInput") is { } input)
        {
            fitting = true;
            grid.RowDefinitions[2].Height = new GridLength(120);
            fitting = false;
            grid.RowDefinitions[2].MinHeight = 60;
            input.MaxHeight = double.PositiveInfinity;
        }
    }

    private void OnHomeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        revealFocused |= args.PropertyName == nameof(HomeViewModel.Selected);

        if (args.PropertyName is nameof(HomeViewModel.Selected) or nameof(HomeViewModel.IsDrawerOpen))
        {
            RevealFocused();
        }
    }

    // The drawer's list stays where the user scrolled it, so it is only brought to the focused workspace once after the focus moved, when it can be seen.
    private void RevealFocused() => Dispatcher.UIThread.Post(
        () =>
        {
            if (revealFocused && home is { IsDrawerOpen: true, SelectedEntry: { } entry } && this.FindControl<ListBox>("DrawerList") is { } drawer)
            {
                revealFocused = false;
                drawer.ScrollIntoView(entry);
            }
        },
        DispatcherPriority.Loaded);

    private void OnChatTextChanged()
    {
        if (DataContext is not HomeViewModel { IsDesktop: true } || this.FindControl<Grid>("ChatGrid") is not { } grid || this.FindControl<TextBox>("ChatInput") is not { } input)
        {
            return;
        }

        if (string.IsNullOrEmpty(input.Text))
        {
            if (restoreHeight is { } height)
            {
                SetChatHeight(grid.RowDefinitions[2], height);
                restoreHeight = null;
            }

            return;
        }

        Dispatcher.UIThread.Post(GrowChatInput, DispatcherPriority.Loaded);
    }

    private void GrowChatInput()
    {
        if (this.FindControl<Grid>("ChatGrid") is not { } grid || this.FindControl<TextBox>("ChatInput") is not { } input || input.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller)
        {
            return;
        }

        RowDefinition row = grid.RowDefinitions[2];
        double current = row.ActualHeight;
        double target = Math.Min(current + scroller.Extent.Height - scroller.Viewport.Height, Math.Max(current, grid.Bounds.Height - 120));

        if (scroller.Extent.Height - scroller.Viewport.Height > 1 && target > current + 0.5)
        {
            restoreHeight ??= current;
            SetChatHeight(row, target);
        }
    }

    private void SetChatHeight(RowDefinition row, double height)
    {
        fitting = true;
        row.Height = new GridLength(height);
        fitting = false;
    }

    // A file that cannot be read must not take the app down, which an exception out of the click would.
    private async Task AttachFile(ChatViewModel chat, Avalonia.Platform.Storage.IStorageFile file)
    {
        try
        {
            await using Stream stream = await file.OpenReadAsync();
            await AttachFile(chat, file.Name, stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            chat.FailAttachment(exception.Message);
        }
    }

    private async Task AttachFile(ChatViewModel chat, string name, Stream stream)
    {
        using MemoryStream buffer = new();
        byte[] block = new byte[81920];

        while (buffer.Length <= chat.MaxAttachment && await stream.ReadAsync(block) is int read and > 0)
        {
            buffer.Write(block, 0, read);
        }

        chat.Attach(name, buffer.ToArray());
    }

    private async void OnAttachClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (Chat is not { } chat || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<Avalonia.Platform.Storage.IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { AllowMultiple = true, Title = "Attach files" });

        foreach (Avalonia.Platform.Storage.IStorageFile file in files)
        {
            await AttachFile(chat, file);
        }
    }

    private void OnChatKeyDown(object? sender, Avalonia.Input.KeyEventArgs args)
    {
        if (args.Key == Avalonia.Input.Key.Enter && args.KeyModifiers is Avalonia.Input.KeyModifiers.Shift or Avalonia.Input.KeyModifiers.Alt)
        {
            args.Handled = true;

            // Shift+Enter is the send button; Alt+Enter queues, as Send Options does for Next when nothing is changed.
            if ((DataContext as HomeViewModel)?.Active is { } active && (args.KeyModifiers == Avalonia.Input.KeyModifiers.Shift ? active.SubmitCommand : active.Chat.QueueCommand) is { } command && command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        if (TextBox.PasteGesture?.Matches(args) == true)
        {
            args.Handled = true;
            _ = Paste(sender as TextBox);
        }
    }

    private async Task Paste(TextBox? box)
    {
        if (Chat is not { } chat || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        using Avalonia.Media.Imaging.Bitmap? bitmap = await clipboard.TryGetBitmapAsync();

        if (bitmap is not null)
        {
            using MemoryStream png = new();
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            chat.Attach("pasted-image.png", png.ToArray());
            return;
        }

        Avalonia.Platform.Storage.IStorageItem[]? items = await clipboard.TryGetFilesAsync();

        if (items is { Length: > 0 })
        {
            foreach (Avalonia.Platform.Storage.IStorageItem item in items)
            {
                if (item is Avalonia.Platform.Storage.IStorageFile file)
                {
                    await AttachFile(chat, file);
                }
            }

            return;
        }

        box?.Paste();
    }

    private void OnCommitMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (sender is Control { DataContext: GitCommitViewModel { CanCheckout: false } })
        {
            args.Cancel = true;
        }
    }

    private void OnRefMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (sender is Control { DataContext: GitRefViewModel { HasMenu: false } })
        {
            args.Cancel = true;
        }
    }

    private bool SessionKey(HomeViewModel home, Avalonia.Input.Key key)
    {
        if (home.Sessions.IsPrompting)
        {
            if (key == Avalonia.Input.Key.Escape)
            {
                home.Sessions.DismissCommand.Execute(null);
                return true;
            }

            // Enter takes a name only; what cannot be undone is confirmed with its button.
            if (key == Avalonia.Input.Key.Enter && home.Sessions.NeedsName && this.FindControl<TextBox>("SessionName") is { IsFocused: true })
            {
                home.Sessions.ConfirmCommand.Execute(null);
                return true;
            }

            return false;
        }

        if (key == Avalonia.Input.Key.Escape && home.Sessions.HasTask)
        {
            home.Sessions.CloseTask();
            return true;
        }

        if (key != Avalonia.Input.Key.Escape || !home.Sessions.IsOpen)
        {
            return false;
        }

        home.Sessions.IsOpen = false;
        return true;
    }

    private bool NameKey(HomeViewModel home, object? source)
    {
        if (source is not TextBox)
        {
            return false;
        }

        AsyncCommand? submit = home.EditMode switch
        {
            EditMode.RenameWorkspace => home.ConfirmRenameCommand,
            EditMode.NewGroup => home.ConfirmNewGroupCommand,
            EditMode.RenameGroup => home.ConfirmRenameGroupCommand,
            _ => null
        };
        submit?.Execute(null);
        return submit is not null;
    }

    // Enter goes ahead with what Send Options shows and Escape cancels it. A list or picker that is open over it keeps both, as Cancel keeps Enter, and a box with lines on a phone, whose keyboard has no other way to start a new one.
    private bool SendOptionsKey(HomeViewModel home, Avalonia.Input.KeyEventArgs args)
    {
        if (home.Active?.SendOptions is not { IsOpen: true } options || args.Source is Visual source && source != this && !this.IsVisualAncestorOf(source) || this.FindControl<Border>("SendOptionsCard") is not { } card)
        {
            return false;
        }

        if (args.Key == Avalonia.Input.Key.Escape)
        {
            options.CancelCommand.Execute(null);
            return true;
        }

        if (args.Key != Avalonia.Input.Key.Enter || args.Source is Control { Name: "SendOptionsCancel" } || !home.IsDesktop && args.Source is TextBox { AcceptsReturn: true })
        {
            return false;
        }

        // Leaving a field has it take what was typed into it.
        card.Focus();
        options.ConfirmCommand.Execute(null);
        return true;
    }

    private void OnPromptKeyDown(object? sender, Avalonia.Input.KeyEventArgs args)
    {
        if (DataContext is HomeViewModel sending && args.KeyModifiers == Avalonia.Input.KeyModifiers.None && SendOptionsKey(sending, args))
        {
            args.Handled = true;
            return;
        }

        if (DataContext is HomeViewModel shown && args.KeyModifiers == Avalonia.Input.KeyModifiers.None && (SessionKey(shown, args.Key) || args.Key == Avalonia.Input.Key.Enter && shown.IsEditing && NameKey(shown, args.Source)))
        {
            args.Handled = true;
            return;
        }

        if (DataContext is HomeViewModel { IsEditing: true } home && args.Key == Avalonia.Input.Key.Escape && args.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            home.CancelEditCommand.Execute(null);
            args.Handled = true;
            return;
        }

        if (DataContext is not HomeViewModel { Active.Git: { IsPrompting: true } git } || args.KeyModifiers != Avalonia.Input.KeyModifiers.None)
        {
            return;
        }

        if (args.Key == Avalonia.Input.Key.Escape)
        {
            git.CancelCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Avalonia.Input.Key.Enter && git.SubmitsOnEnter)
        {
            git.ConfirmCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void OnFileTapped(object? sender, Avalonia.Input.TappedEventArgs args)
    {
        if (held)
        {
            held = false;
            return;
        }

        if (sender is TreeView { DataContext: FilesViewModel files } && NodeOf(args.Source) is { } node)
        {
            _ = files.Choose(node);
        }
    }

    private void OnFileDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs args)
    {
        if (sender is TreeView { DataContext: FilesViewModel files } && NodeOf(args.Source) is { } node)
        {
            if (node.IsFolder)
            {
                if (files.ShowNavigation)
                {
                    _ = files.Enter(node);
                }
            }
            else if (!files.ShowNavigation)
            {
                files.Run(node);
            }
        }
    }

    private void OnFileHoldStart(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        EndFileHold();
        held = false;

        if (args.Pointer.Type == Avalonia.Input.PointerType.Touch && DataContext is HomeViewModel { IsDesktop: false, Active.Files: { } files } && NodeOf(args.Source) is { IsRunnable: true } node)
        {
            holdStart = args.GetPosition(this);
            CancellationTokenSource source = new();
            fileHold = source;
            _ = RunAfterHold(files, node, source.Token);
        }
    }

    private async Task RunAfterHold(FilesViewModel files, FileNode node, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(FileHoldDelay, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        fileHold = null;
        held = true;
        files.Run(node);
    }

    private void OnFileHoldMoved(object? sender, Avalonia.Input.PointerEventArgs args)
    {
        if (fileHold is not null && (Math.Abs(args.GetPosition(this).X - holdStart.X) > holdSlop || Math.Abs(args.GetPosition(this).Y - holdStart.Y) > holdSlop))
        {
            EndFileHold();
        }
    }

    private void OnFileHoldEnd(object? sender, Avalonia.Input.PointerReleasedEventArgs args) => EndFileHold();

    private void EndFileHold()
    {
        fileHold?.Cancel();
        fileHold = null;
    }

    private void OnFilePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (NodeOf(args.Source) is not null && args.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            args.Handled = true;
        }
    }

    private void OnFileMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (DataContext is not HomeViewModel { IsDesktop: true })
        {
            args.Cancel = true;
        }
    }

    private async void OnCopyNameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (NodeOf(sender) is { } node && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(node.Name);
        }
    }

    private async void OnCopyRelativePathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (NodeOf(sender) is { } node && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(node.Path);
        }
    }

    private async void OnCopyAbsolutePathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (NodeOf(sender) is { } node && (DataContext as HomeViewModel)?.Active is { } workspace && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(workspace.AbsolutePath(node.Path));
        }
    }

    private void CloseSessions(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (DataContext is HomeViewModel home)
        {
            home.Sessions.IsOpen = false;
        }
    }

    private void OnTopBarPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (DataContext is not HomeViewModel { IsDesktop: true } || TopLevel.GetTopLevel(this) is not Window window || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (args.Source is Visual source && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(source, true) is not null)
        {
            return;
        }

        if (args.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            window.BeginMoveDrag(args);
        }
    }

    private async void OnCopyPathClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((DataContext as HomeViewModel)?.Active is { } workspace && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(workspace.Workspace.Folder);
        }
    }

    private void OnOpenFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((DataContext as HomeViewModel)?.Active is not { } workspace)
        {
            return;
        }

        string folder = workspace.Workspace.Folder;

        if (!Directory.Exists(folder))
        {
            workspace.Files.Fail($"The folder {folder} is on the server, not on this machine.");
            return;
        }

        string command = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";

        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(command, $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            workspace.Files.Fail($"The folder could not be opened: {exception.Message}");
        }
    }

    private async void OnCopyOutputClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if ((DataContext as HomeViewModel)?.Active?.Scripts.Displayed is { } script && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(script.Output);
        }
    }

    private void OnOutputScrolled(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer { DataContext: ScriptsViewModel { AutoScroll: true } } scroller && args.ExtentDelta.Y > 0)
        {
            scroller.ScrollToEnd();
        }
    }

    private void OnOutputAutoScrollToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true })
        {
            this.FindControl<ScrollViewer>("OutputScroller")?.ScrollToEnd();
        }
    }

    private void OnChatAutoScrollToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true } && this.FindControl<ScrollViewer>("MessageScroller") is { } scroller)
        {
            if (scroller.DataContext is ChatViewModel chat)
            {
                chat.IsAtEnd = true;
            }

            scroller.ScrollToEnd();
        }
    }

    private void OnMarkdownScrolled(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer { Content: MarkdownView view } scroller)
        {
            view.UpdateTrail(scroller.Offset.Y);
        }
    }

    private void OnOutlineChosen(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Control { Tag: int index })
        {
            this.FindControl<MarkdownView>("MarkdownPreview")?.ScrollTo(index);
            this.FindControl<Button>("MarkdownOutlineButton")?.Flyout?.Hide();
        }
    }

    private void OnTrailChosen(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Control { Tag: int index })
        {
            this.FindControl<MarkdownView>("MarkdownPreview")?.ScrollTo(index);
        }
    }

    private void OnFoldLevelChosen(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Control control && int.TryParse($"{control.Tag}", CultureInfo.InvariantCulture, out int level))
        {
            this.FindControl<MarkdownView>("MarkdownPreview")?.FoldToLevel(level);
            this.FindControl<Button>("MarkdownLevelButton")?.Flyout?.Hide();
        }
    }

    private void OnFindToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true })
        {
            FocusFind();
        }
    }

    private void OnSearchToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("SearchBox")?.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void FocusFind() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (this.FindControl<TextBox>("FindBox") is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }, Avalonia.Threading.DispatcherPriority.Background);

    /// <inheritdoc />
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs args)
    {
        base.OnKeyDown(args);

        if (args.Key == Avalonia.Input.Key.F && args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && (DataContext as HomeViewModel)?.Active?.Files is { IsFindable: true } files)
        {
            files.Find.IsOpen = true;
            FocusFind();
            args.Handled = true;
        }
    }

    private async void OnMessagesScrolled(object? sender, ScrollChangedEventArgs args)
    {
        if (sender is not ScrollViewer scroller || scroller.DataContext is not ChatViewModel chat)
        {
            return;
        }

        if (args.ExtentDelta.Y == 0 && args.OffsetDelta.Y != 0)
        {
            CaptureOnScreen(scroller);
        }

        bool bottom = scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2;

        // Moving to the end leaves it there; moving up takes it away, also in a change that brought a new height with it.
        if (args.OffsetDelta.Y < 0 || args.OffsetDelta.Y > 0 && args.ExtentDelta.Y == 0)
        {
            chat.IsAtEnd = bottom;
        }

        // A chat left at its end stays there while it grows. Anywhere else a height that grows is not new output, which alone moves it: older messages, text that wraps anew and content that is drawn late all grow it.
        if (args.ExtentDelta.Y > 0 && chat.AutoScroll && chat.IsAtEnd)
        {
            scroller.ScrollToEnd();
            return;
        }

        if ((args.OffsetDelta.Y != 0 && args.ExtentDelta.Y == 0) || anchor?.IsAttachedToVisualTree() != true)
        {
            CaptureAnchor(scroller);
        }

        bool scrollable = scroller.Extent.Height > scroller.Viewport.Height + 1;

        if (scrollable && scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 1)
        {
            chat.TrimToNewest();
            return;
        }

        if (args.ExtentDelta.Y == 0 && args.OffsetDelta.Y < 0 && scroller.Offset.Y <= 1)
        {
            await LoadOlderMessages(chat);
        }
    }

    private async void OnMessagesWheel(object? sender, Avalonia.Input.PointerWheelEventArgs args)
    {
        if (sender is ScrollViewer { DataContext: ChatViewModel chat } scroller && args.Delta.Y > 0 && scroller.Offset.Y <= 1)
        {
            await LoadOlderMessages(chat);
        }
    }

    private async Task LoadOlderMessages(ChatViewModel chat)
    {
        if (!chat.HasMore || loadingOlder)
        {
            return;
        }

        loadingOlder = true;

        try
        {
            await chat.LoadOlder();
        }
        finally
        {
            loadingOlder = false;
        }
    }

    private void CaptureAnchor(ScrollViewer scroller)
    {
        anchor = null;

        if (this.FindControl<ItemsControl>("MessageList") is not { ItemsPanelRoot: { } panel } list)
        {
            return;
        }

        foreach (Control child in panel.Children)
        {
            if (ContentTop(child, list) is { } top && top + child.Bounds.Height - scroller.Offset.Y > 0.5)
            {
                anchor = child;
                anchorContent = top;
                anchorIndex = IndexOfMessage(child);
                return;
            }
        }
    }

    private double HowFarAnchorMoved()
    {
        if (anchor is not { } kept || !kept.IsAttachedToVisualTree() || this.FindControl<ItemsControl>("MessageList") is not { } list || ContentTop(kept, list) is not { } top)
        {
            return 0;
        }

        return top - anchorContent;
    }

    private int IndexOfMessage(Control child) => child.DataContext is ChatMessage message && followed is { } chat ? chat.Messages.IndexOf(message) : -1;

    private void Compensate(ScrollViewer scroller)
    {
        double moved = HowFarAnchorMoved();

        if (Math.Abs(moved) < 0.5)
        {
            return;
        }

        anchorContent += moved;
        int index = anchor is { } kept ? IndexOfMessage(kept) : -1;

        bool removed = index >= 0 && index < anchorIndex;

        if (index >= 0)
        {
            anchorIndex = index;
        }

        if (removed)
        {
            return;
        }

        scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y + moved));
    }

    private double? ContentTop(Control child, ItemsControl list) => child.TranslatePoint(new Avalonia.Point(0, 0), list) is { } at ? at.Y + list.Margin.Top : null;

    private void OnMessagesLaidOut(object? sender, EventArgs args)
    {
        if (sender is not ScrollViewer scroller || followed is null)
        {
            return;
        }

        if (returning is not null && Return(scroller))
        {
            return;
        }

        if (anchor?.IsAttachedToVisualTree() != true)
        {
            CaptureAnchor(scroller);
            return;
        }

        Compensate(scroller);
        CaptureOnScreen(scroller);
    }

    private void OnMessagesReplaced(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && followed is { } chat && !(chat.AutoScroll && chat.IsAtEnd) && onScreen.Count > 0)
        {
            returning = [.. onScreen];
            returnAttempts = 0;
        }
    }

    private void CaptureOnScreen(ScrollViewer scroller)
    {
        onScreen.Clear();

        if (this.FindControl<ItemsControl>("MessageList") is not { ItemsPanelRoot: { } panel } list)
        {
            return;
        }

        foreach (Control child in panel.Children)
        {
            if (child.DataContext is ChatMessage message && ContentTop(child, list) is { } top && top + child.Bounds.Height - scroller.Offset.Y > 0.5 && top - scroller.Offset.Y < scroller.Viewport.Height)
            {
                onScreen.Add((message, top - scroller.Offset.Y));

                if (onScreen.Count >= 40)
                {
                    return;
                }
            }
        }
    }

    private bool Return(ScrollViewer scroller)
    {
        if (returning is not { } wanted || this.FindControl<ItemsControl>("MessageList") is not { ItemsPanelRoot: { } panel } list)
        {
            returning = null;
            return false;
        }

        foreach ((ChatMessage message, double top) in wanted)
        {
            foreach (Control child in panel.Children)
            {
                if (child.DataContext is ChatMessage shown && shown.Role == message.Role && shown.Text == message.Text && (shown.Time is null || message.Time is null || shown.Time == message.Time) && ContentTop(child, list) is { } content)
                {
                    returning = null;
                    scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, content - top));
                    anchor = child;
                    anchorContent = content;
                    anchorIndex = IndexOfMessage(child);
                    return true;
                }
            }
        }

        if (++returnAttempts > 5)
        {
            returning = null;
        }

        return false;
    }

    private void OnMessagesChanged(object? sender, EventArgs args)
    {
        loadingOlder = false;
        anchor = null;

        if (sender is not ScrollViewer scroller)
        {
            return;
        }

        if (!laidOutHooked)
        {
            laidOutHooked = true;
            scroller.LayoutUpdated += OnMessagesLaidOut;
        }

        if (followed is not null)
        {
            followed.Loaded -= ScrollMessagesToEnd;
            followed.OutputArrived -= FollowOutput;
            followed.Messages.CollectionChanged -= OnMessagesReplaced;
        }

        onScreen.Clear();
        returning = null;
        followed = scroller.DataContext as ChatViewModel;

        if (followed is not null)
        {
            followed.Loaded += ScrollMessagesToEnd;
            followed.OutputArrived += FollowOutput;
            followed.Messages.CollectionChanged += OnMessagesReplaced;
        }

        ScrollMessagesToEnd();
    }

    private void FollowOutput()
    {
        if (followed is { AutoScroll: true })
        {
            // What was on screen is not returned to, and the layouts the output still needs keep the end in view.
            returning = null;
            ScrollMessagesToEnd();
        }
    }

    private void ScrollMessagesToEnd()
    {
        if (this.FindControl<ScrollViewer>("MessageScroller") is { } scroller)
        {
            if (followed is not null)
            {
                followed.IsAtEnd = true;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(scroller.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void CloseSendOptions(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (DataContext is HomeViewModel { Active: { } active })
        {
            active.SendOptions.IsOpen = false;
        }
    }

    private void CloseUsage(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (DataContext is HomeViewModel model)
        {
            model.IsUsageOpen = false;
        }
    }

    private void CloseDrawer(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        if (DataContext is HomeViewModel model)
        {
            model.IsDrawerOpen = false;
        }
    }
}
