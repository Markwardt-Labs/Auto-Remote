namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The tabs shown for a focused workspace.
/// </summary>
internal enum WorkspaceTab
{
    /// <summary>
    /// The conversation with the AI.
    /// </summary>
    Chat,

    /// <summary>
    /// The files in the workspace's folder.
    /// </summary>
    Files,

    /// <summary>
    /// The git history of the workspace.
    /// </summary>
    History,

    /// <summary>
    /// The workspace's settings.
    /// </summary>
    Settings
}
