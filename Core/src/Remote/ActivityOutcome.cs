namespace Markwardt.AutoRemote;

/// <summary>
/// How a piece of work went.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityOutcome
{
    /// <summary>
    /// It is under way.
    /// </summary>
    Running,

    /// <summary>
    /// It waits to be tried again.
    /// </summary>
    Waiting,

    /// <summary>
    /// It ended well.
    /// </summary>
    Done,

    /// <summary>
    /// The AI said it could not finish it.
    /// </summary>
    Unfinished,

    /// <summary>
    /// It failed and is not tried again.
    /// </summary>
    Failed,

    /// <summary>
    /// The user stopped it.
    /// </summary>
    Stopped
}
