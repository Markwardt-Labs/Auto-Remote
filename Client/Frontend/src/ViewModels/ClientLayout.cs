namespace Markwardt.AutoRemote.Client;

/// <summary>
/// How a focused workspace lays out its conversation and files.
/// </summary>
public enum ClientLayout
{
    /// <summary>
    /// One at a time, switched with tabs along the bottom, for narrow touch screens.
    /// </summary>
    Tabs,

    /// <summary>
    /// Side by side as the file system, the content of the selected file and the conversation, with resizable separators; the layout of the desktop client, which also has what only suits a computer.
    /// </summary>
    Panels
}
