using Avalonia.Controls.ApplicationLifetimes;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Android head of the client application; hosts the root view as the single view.
/// </summary>
public sealed class App : ClientApp
{
    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = CreateMainView(typeof(App).Assembly, ClientLayout.Tabs, null, new AndroidChime(new ChimeSound()), new AndroidReadyNotifier(), new AndroidWorkKeeper());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
