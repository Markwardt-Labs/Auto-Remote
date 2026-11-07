namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Which prompt every run of a loop sends.
/// </summary>
internal enum LoopPromptKind
{
    /// <summary>
    /// The review prompt of the workspace, or the global one.
    /// </summary>
    Review,

    /// <summary>
    /// The expand prompt of the workspace, or the global one.
    /// </summary>
    Expand,

    /// <summary>
    /// The user's own prompt.
    /// </summary>
    Custom
}
