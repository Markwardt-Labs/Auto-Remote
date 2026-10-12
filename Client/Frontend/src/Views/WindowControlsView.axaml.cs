namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The minimize, maximize or restore, and close buttons of a window without a title bar of its own.
/// </summary>
public sealed partial class WindowControlsView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WindowControlsView" /> class.
    /// </summary>
    public WindowControlsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private Window? subscribed;

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void Subscribe()
    {
        Unsubscribe();

        if (Owner is { } window)
        {
            subscribed = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            ShowState(window.WindowState);
        }
    }

    private void Unsubscribe()
    {
        if (subscribed is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            subscribed = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Window.WindowStateProperty)
        {
            ShowState((WindowState)args.NewValue!);
        }
    }

    private void ShowState(WindowState state)
    {
        this.FindControl<Button>("MaximizeButton")!.IsVisible = state != WindowState.Maximized;
        this.FindControl<Button>("RestoreButton")!.IsVisible = state == WindowState.Maximized;
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (Owner is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeRestoreClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (Owner is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Owner?.Close();
}
