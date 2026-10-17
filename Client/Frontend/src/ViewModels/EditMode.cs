namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The dialogs the main screen can show over its content.
/// </summary>
internal enum EditMode
{
    /// <summary>
    /// No dialog.
    /// </summary>
    None,

    /// <summary>
    /// Renaming the focused workspace.
    /// </summary>
    RenameWorkspace,

    /// <summary>
    /// Confirming deletion of the focused workspace.
    /// </summary>
    DeleteWorkspace,

    /// <summary>
    /// Naming a new group.
    /// </summary>
    NewGroup,

    /// <summary>
    /// Renaming a group.
    /// </summary>
    RenameGroup,

    /// <summary>
    /// Confirming deletion of a group.
    /// </summary>
    DeleteGroup,

    /// <summary>
    /// Choosing the group to move a workspace into.
    /// </summary>
    MoveWorkspace,

    /// <summary>
    /// Choosing the group to move a group into.
    /// </summary>
    MoveGroup,

    /// <summary>
    /// Confirming that the work in the focused workspace is stopped, keeping what it did.
    /// </summary>
    StopWork,

    /// <summary>
    /// Confirming that the work in the focused workspace is stopped and what it did reverted.
    /// </summary>
    CancelWork
}
