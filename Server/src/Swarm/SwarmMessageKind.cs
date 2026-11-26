namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What a package between paired servers is.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SwarmMessageKind
{
    /// <summary>
    /// Opens a network session and proves who sends it.
    /// </summary>
    Hello,

    /// <summary>
    /// A task for the receiver to do.
    /// </summary>
    Task,

    /// <summary>
    /// The result of a task.
    /// </summary>
    Result,

    /// <summary>
    /// How tasks the receiver gave are getting on.
    /// </summary>
    Progress,

    /// <summary>
    /// A task the sender gave is cancelled.
    /// </summary>
    Cancel,

    /// <summary>
    /// What the sender can do.
    /// </summary>
    Profile,

    /// <summary>
    /// The packages the sender has handled, which need not be sent again.
    /// </summary>
    Ack
}
