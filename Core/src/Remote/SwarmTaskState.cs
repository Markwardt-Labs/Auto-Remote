namespace Markwardt.AutoRemote;

/// <summary>
/// Where a delegated task stands.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwarmTaskState
{
    /// <summary>
    /// It waits for tasks it depends on.
    /// </summary>
    Waiting,

    /// <summary>
    /// It waits for a worker to take it.
    /// </summary>
    Pending,

    /// <summary>
    /// It was given to a worker, which has not confirmed receiving it, such as while a drive carries it.
    /// </summary>
    InTransit,

    /// <summary>
    /// The worker has it and it waits to start there.
    /// </summary>
    Queued,

    /// <summary>
    /// The worker works on it.
    /// </summary>
    Running,

    /// <summary>
    /// It finished, and its result is here.
    /// </summary>
    Done,

    /// <summary>
    /// It could not be done.
    /// </summary>
    Failed,

    /// <summary>
    /// It was cancelled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The worker stopped being heard from while it had the task. A result that still arrives is taken.
    /// </summary>
    Lost
}
