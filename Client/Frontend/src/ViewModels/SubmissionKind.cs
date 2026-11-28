namespace Markwardt.AutoRemote.Client;

/// <summary>
/// What Send Options submits, which follows from the button it is opened from.
/// </summary>
internal enum SubmissionKind
{
    /// <summary>
    /// What is typed and attached in the message box.
    /// </summary>
    Custom,

    /// <summary>
    /// The review prompt of the workspace.
    /// </summary>
    Review,

    /// <summary>
    /// The expand prompt of the workspace.
    /// </summary>
    Expand
}
