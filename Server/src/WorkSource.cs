namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Where work for a workspace comes from, which decides its place in the queue of the local AI.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum WorkSource
{
    /// <summary>
    /// The user of this server, or this server itself on the user's behalf.
    /// </summary>
    Direct,

    /// <summary>
    /// A run of a loop of the user, which counts as direct work but ends with the loop.
    /// </summary>
    Loop,

    /// <summary>
    /// The report of a job of delegated tasks, which counts as direct work. The swarm keeps it and gives it again until it started, so it does not wait across a restart.
    /// </summary>
    Report,

    /// <summary>
    /// A task another server delegated, which starts only after all direct work that could start.
    /// </summary>
    Delegated
}
