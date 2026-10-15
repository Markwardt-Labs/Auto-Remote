namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Home screen.
/// </summary>
public sealed partial class HomeView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HomeView" /> class.
    /// </summary>
    public HomeView()
    {
        InitializeComponent();
        AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnFilePointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        this.FindControl<TextBox>("ChatInput")?.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnChatKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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

    private bool loadingOlder;

    private bool fitting;

    private ChatViewModel? followed;

    private double? restoreHeight;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs args)
    {
        base.OnDataContextChanged(args);

        if (DataContext is HomeViewModel { IsDesktop: true } && this.FindControl<Grid>("ChatGrid") is { } grid && this.FindControl<TextBox>("ChatInput") is { } input)
        {
            fitting = true;
            grid.RowDefinitions[2].Height = new GridLength(120);
            fitting = false;
            grid.RowDefinitions[2].MinHeight = 60;
            input.MaxHeight = double.PositiveInfinity;
        }
    }

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

    private ChatViewModel? Chat => (DataContext as HomeViewModel)?.Active?.Chat;

    private static FileNode? NodeOf(object? source) => (source as StyledElement)?.DataContext as FileNode;

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
            await using Stream stream = await file.OpenReadAsync();
            await AttachFile(chat, file.Name, stream);
        }
    }

    private void OnChatKeyDown(object? sender, Avalonia.Input.KeyEventArgs args)
    {
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
                    await using Stream stream = await file.OpenReadAsync();
                    await AttachFile(chat, file.Name, stream);
                }
            }

            return;
        }

        box?.Paste();
    }

    private void OnFileTapped(object? sender, Avalonia.Input.TappedEventArgs args)
    {
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
            else
            {
                files.Run(node);
            }
        }
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
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true })
        {
            this.FindControl<ScrollViewer>("MessageScroller")?.ScrollToEnd();
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

        if (args.ExtentDelta.Y > 0)
        {
            if (loadingOlder)
            {
                scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y + args.ExtentDelta.Y);
                loadingOlder = false;
                return;
            }

            if (chat.AutoScroll)
            {
                scroller.ScrollToEnd();
                return;
            }
        }

        if (scroller.Offset.Y <= 1 && chat.HasMore && !loadingOlder)
        {
            loadingOlder = true;

            if (!await chat.LoadOlder())
            {
                loadingOlder = false;
            }
        }
    }

    private void OnMessagesChanged(object? sender, EventArgs args)
    {
        loadingOlder = false;

        if (sender is not ScrollViewer scroller)
        {
            return;
        }

        if (followed is not null)
        {
            followed.Loaded -= ScrollMessagesToEnd;
        }

        followed = scroller.DataContext as ChatViewModel;

        if (followed is not null)
        {
            followed.Loaded += ScrollMessagesToEnd;
        }

        ScrollMessagesToEnd();
    }

    private void ScrollMessagesToEnd()
    {
        if (this.FindControl<ScrollViewer>("MessageScroller") is { } scroller)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(scroller.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Background);
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
