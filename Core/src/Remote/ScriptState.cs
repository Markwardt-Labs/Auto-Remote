namespace Markwardt.AutoRemote;

/// <summary>
/// How far a script or task run has come.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScriptState
{
    /// <summary>
    /// A script of a task that waits for the scripts before it to finish.
    /// </summary>
    Pending,

    /// <summary>
    /// The run is in progress.
    /// </summary>
    Running,

    /// <summary>
    /// The run finished with exit code zero; a task succeeds when every script in it does.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The run finished with a failure, or could not start.
    /// </summary>
    Failed,

    /// <summary>
    /// The run was stopped by a client.
    /// </summary>
    Stopped,

    /// <summary>
    /// A script of a task that never ran because the task failed or was stopped before its turn.
    /// </summary>
    Skipped
}
