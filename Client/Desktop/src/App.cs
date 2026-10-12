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
            Window window = new()
            {
                Title = "Auto Remote",
                Width = 1280,
                Height = 800,
                MinWidth = 640,
                MinHeight = 480,
                Content = CreateMainView(typeof(App).Assembly, ClientLayout.Panels, DataFolder(desktop.Args)),
            };

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
