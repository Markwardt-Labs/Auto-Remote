namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Avalonia application shared by every client head: the theme plus the root view wired to the real services.
/// </summary>
public class ClientApp : Avalonia.Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Builds the root view wired to the real services, for heads that host the view themselves.
    /// </summary>
    /// <param name="head">The executable assembly of the head, whose version is reported to the server.</param>
    /// <param name="layout">How a focused workspace lays out its conversation and files.</param>
    /// <param name="dataFolder">The folder holding the saved settings, or <see langword="null" /> for the <c>AutoRemote</c> folder of the user's application data.</param>
    /// <returns>The root view, already starting up.</returns>
    public MainView CreateMainView(Assembly head, ClientLayout layout, string? dataFolder = null)
    {
        string settingsPath = Path.Combine(dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoRemote"), "settings.json");
        IAppInfo info = new IAppInfo.Factory().Create(head);
        MainViewModel model = new(info, new IServerConnection.Factory(new IMessageChannel.Factory(), new ConsoleLog()), new SettingsStore(settingsPath), layout);
        MainView view = new() { DataContext = model };
        _ = model.Start();
        return view;
    }
}
