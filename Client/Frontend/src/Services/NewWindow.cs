namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Opens another window of the app as a process of its own; only a head that can do that implements it, which is the desktop one.
/// </summary>
public interface INewWindow
{
    /// <summary>
    /// Starts another instance of the app without waiting for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The app could not be started.</exception>
    void Open();
}
