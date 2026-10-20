namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Avalonia application shared by every client head: the theme plus the root view wired to the real services.
/// </summary>
public class ClientApp : Avalonia.Application
{
    private readonly TimeSpan awayLimit = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Builds the root view wired to the real services, for heads that host the view themselves.
    /// </summary>
    /// <param name="head">The executable assembly of the head, whose version is reported to the server.</param>
    /// <param name="layout">How a focused workspace lays out its conversation and files.</param>
    /// <param name="dataFolder">The folder holding the saved settings and message box drafts, or <see langword="null" /> for the <c>AutoRemote</c> folder of the user's application data.</param>
    /// <param name="chime">Plays the ding when a workspace becomes ready again, or <see langword="null" /> for silence.</param>
    /// <param name="notifier">Tells the user outside the app that a workspace they gave work to is ready, or <see langword="null" /> for no notifications.</param>
    /// <param name="keeper">Keeps the app alive while work this client gave a workspace runs, or <see langword="null" /> when the app is not stopped in the background.</param>
    /// <param name="recorder">Records speech for the voice input button of the message box, or <see langword="null" /> when this client cannot record, which hides the button.</param>
    /// <returns>The root view, already starting up.</returns>
    public MainView CreateMainView(Assembly head, ClientLayout layout, string? dataFolder = null, IChime? chime = null, IReadyNotifier? notifier = null, IWorkKeeper? keeper = null, IVoiceRecorder? recorder = null)
    {
        string folder = dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoRemote");
        IAppInfo info = new IAppInfo.Factory().Create(head);
        MainViewModel model = new(info, new IServerConnection.Factory(new IMessageChannel.Factory(), new ConsoleLog()), new SettingsStore(Path.Combine(folder, "settings.json")), layout, new DraftStore(Path.Combine(folder, "drafts.json")), chime, notifier, keeper, recorder);
        MainView view = new() { DataContext = model };
        _ = model.Start();

        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
        {
            DateTime? left = null;
            lifetime.Deactivated += (_, _) => left = DateTime.UtcNow;
            lifetime.Activated += (_, _) =>
            {
                if (left is { } at && DateTime.UtcNow - at > awayLimit)
                {
                    _ = model.Resume();
                }

                left = null;
            };
        }

        return view;
    }
}
