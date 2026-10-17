using Android.Content;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Keeps the app alive in the background with <see cref="WorkService" /> while any workspace is held, and says which in its notification.
/// </summary>
internal sealed class AndroidWorkKeeper : IWorkKeeper
{
    private readonly Context context = global::Android.App.Application.Context;

    private readonly Dictionary<Guid, string> held = [];

    private readonly Lock gate = new();

    /// <summary>
    /// Gets the name of the intent extra that carries the text of the notification to the service.
    /// </summary>
    public static string TextKey => "text";

    /// <inheritdoc />
    public void Hold(Guid workspace, string name)
    {
        string text;
        bool first;

        lock (gate)
        {
            first = held.Count == 0;
            held[workspace] = name;
            text = Text();
        }

        Update(first, text);
    }

    /// <inheritdoc />
    public void Release(Guid workspace)
    {
        string text;
        bool empty;

        lock (gate)
        {
            if (!held.Remove(workspace))
            {
                return;
            }

            empty = held.Count == 0;
            text = Text();
        }

        if (empty)
        {
            context.StopService(new Intent(context, typeof(WorkService)));
        }
        else
        {
            new AndroidWorkNotification(context).Show(text);
        }
    }

    private string Text() => held.Count == 1 ? $"{held.Values.First()} is working" : $"{held.Count} workspaces are working";

    private void Update(bool start, string text)
    {
        if (!start)
        {
            new AndroidWorkNotification(context).Show(text);
            return;
        }

        try
        {
            Intent intent = new Intent(context, typeof(WorkService)).PutExtra(TextKey, text)!;

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
        }
    }
}
