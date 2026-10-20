using Avalonia.Controls.ApplicationLifetimes;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The desktop head of the client application; hosts the root view in a window.
/// </summary>
public sealed class App : ClientApp
{
    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DesktopReadyNotifier notifier = new();
            MainView view = CreateMainView(typeof(App).Assembly, ClientLayout.Panels, DataFolder(desktop.Args), new DesktopChime(new ChimeSound()), notifier, null, new DesktopVoiceRecorder());
            Window window = new()
            {
                Title = "Auto Remote",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Markwardt.AutoRemote.DesktopClient/Assets/app.png"))),
                Width = 1280,
                Height = 800,
                MinWidth = 640,
                MinHeight = 480,
                WindowDecorations = WindowDecorations.None,
                Content = view,
            };

            window.Bind(Window.TitleProperty, new Binding("DataContext.WindowTitle") { Source = view });
            window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
            WindowState restore = WindowState.Maximized;
            window.PropertyChanged += (_, args) =>
            {
                if (args.Property == Window.WindowStateProperty && window.WindowState != WindowState.Minimized)
                {
                    restore = window.WindowState;
                }
            };
            notifier.Opened += workspace => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = restore;
                }

                window.Show();
                window.Activate();
                _ = view.Open(workspace);
            });
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private string? DataFolder(string[]? args)
    {
        int index = Array.IndexOf(args ?? [], "--data-folder");
        return index >= 0 && index + 1 < args!.Length ? Path.GetFullPath(args[index + 1]) : null;
    }
}
