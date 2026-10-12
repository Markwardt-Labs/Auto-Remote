namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Home screen.
/// </summary>
public sealed partial class HomeView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HomeView" /> class.
    /// </summary>
    public HomeView() => InitializeComponent();

    private bool loadingOlder;

    private void OnFileDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs args)
    {
        if (sender is TreeView { DataContext: FilesViewModel files } && (args.Source as StyledElement)?.DataContext is FileNode { IsFolder: true } node)
        {
            _ = files.Enter(node);
        }
    }

    private void OnFindToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true })
        {
            FocusFind();
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

        if (args.Key == Avalonia.Input.Key.F && args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && (DataContext as HomeViewModel)?.Active?.Files is { IsTextPreview: true } files)
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

            if (scroller.Offset.Y - args.OffsetDelta.Y + scroller.Viewport.Height >= scroller.Extent.Height - args.ExtentDelta.Y - 2)
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

        if (sender is ScrollViewer scroller)
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
