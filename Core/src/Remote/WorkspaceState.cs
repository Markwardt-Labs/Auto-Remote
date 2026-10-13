namespace Markwardt.AutoRemote;

/// <summary>
/// What a workspace is doing. A workspace is in exactly one state, and it only leaves <see cref="Ready" /> for another state, and only returns to <see cref="Ready" /> before entering a different one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceState
{
    /// <summary>
    /// Nothing is running, so any work can start.
    /// </summary>
    Ready,

    /// <summary>
    /// The AI is working on a prompt. More AI input can still be given and joins that work.
    /// </summary>
    Working,

    /// <summary>
    /// A script or task is running.
    /// </summary>
    Running,

    /// <summary>
    /// A git action is running, possibly with the AI resolving conflicts for it.
    /// </summary>
    Managing
}
