namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How a delegated task ended on its worker.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SwarmOutcome
{
    /// <summary>
    /// The worker finished it.
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
    /// The worker lacks commits the bundle builds on, so the task is sent again with the whole history.
    /// </summary>
    MissingHistory
}
