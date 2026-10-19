using Avalonia.Controls.ApplicationLifetimes;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Android head of the client application; hosts the root view as the single view.
/// </summary>
public sealed class App : ClientApp
{
    private MainView? view;

    private Guid? requested;

    /// <summary>
    /// Brings a workspace to the front, as opening its notification does; when the app has not built its view yet, the request waits for it.
    /// </summary>
    /// <param name="workspaceId">The workspace identifier.</param>
    public void OpenWorkspace(Guid workspaceId)
    {
        if (view is null)
        {
            requested = workspaceId;
        }
        else
        {
            _ = view.Open(workspaceId);
        }
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            view = CreateMainView(typeof(App).Assembly, ClientLayout.Tabs, null, new AndroidChime(new ChimeSound()), new AndroidReadyNotifier(), new AndroidWorkKeeper());
            singleView.MainView = view;

            if (requested is { } workspace)
            {
                requested = null;
                _ = view.Open(workspace);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
