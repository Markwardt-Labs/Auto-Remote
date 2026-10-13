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
            MainView view = CreateMainView(typeof(App).Assembly, ClientLayout.Panels, DataFolder(desktop.Args));
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
