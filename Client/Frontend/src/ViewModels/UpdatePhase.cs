namespace Markwardt.AutoRemote.Client;

/// <summary>
/// How far an update the user was offered has come.
/// </summary>
internal enum UpdatePhase
{
    /// <summary>
    /// Nothing is happening; the update, if there is one, is only offered.
    /// </summary>
    Idle,

    /// <summary>
    /// The confirmation is showing.
    /// </summary>
    Confirming,

    /// <summary>
    /// The server is updating or the client is downloading and installing.
    /// </summary>
    Working,

    /// <summary>
    /// The server was updated and restarts, and nothing more is left to do on this device.
    /// </summary>
    Waiting,

    /// <summary>
    /// The client handed the new version to the installer of the system, which finishes the update.
    /// </summary>
    Finished
}
