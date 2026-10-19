namespace Markwardt.AutoRemote;

/// <summary>
/// What a workspace is doing. A workspace is in exactly one state. It only leaves <see cref="Ready" /> for another state, and only returns to <see cref="Ready" /> before entering a different one, except that <see cref="Working" /> can be moved to <see cref="Paused" /> and back, and work in <see cref="Working" />, <see cref="Paused" />, <see cref="Running" /> or <see cref="Managing" /> can be moved to <see cref="Stopping" /> (and AI work to <see cref="Canceling" />), which end in <see cref="Ready" />.
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
    Managing,

    /// <summary>
    /// The AI's work is paused: it keeps its place and nothing else can start until it is resumed, stopped or cancelled. No more AI input is taken.
    /// </summary>
    Paused,

    /// <summary>
    /// The work is being stopped. It ends at once and keeps what it did. This state cannot be stopped or cancelled.
    /// </summary>
    Stopping,

    /// <summary>
    /// The work is being stopped and what it did reverted. This state cannot be stopped or cancelled.
    /// </summary>
    Canceling
}
