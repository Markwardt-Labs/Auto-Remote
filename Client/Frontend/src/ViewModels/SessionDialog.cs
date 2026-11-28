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
    /// The new name of a side session.
    /// </summary>
    Rename,

    /// <summary>
    /// Whether to discard a side session.
    /// </summary>
    Delete,

    /// <summary>
    /// Whether to bring a side session into the main one.
    /// </summary>
    Integrate,

    /// <summary>
    /// Whether to bring a side session into the main one by itself once its work is done.
    /// </summary>
    IntegrateWhenFinished,

    /// <summary>
    /// Whether to cancel bringing a side session in.
    /// </summary>
    CancelIntegration
}
