namespace Markwardt.AutoRemote.Client;

/// <summary>
/// What the session list asks the user before it acts.
/// </summary>
internal enum SessionDialog
{
    /// <summary>
    /// Nothing is asked.
    /// </summary>
    None,

    /// <summary>
    /// What a new side session is like.
    /// </summary>
    New,

    /// <summary>
    /// The new name of a side session.
    /// </summary>
    Rename,

    /// <summary>
    /// Whether to discard a side session.
    /// </summary>
    Delete,

    /// <summary>
    /// Whether to integrate a side session.
    /// </summary>
    Integrate,

    /// <summary>
    /// Whether to integrate a side session by itself once it is finished.
    /// </summary>
    IntegrateWhenFinished,

    /// <summary>
    /// Whether to cancel an integration.
    /// </summary>
    CancelIntegration
}
