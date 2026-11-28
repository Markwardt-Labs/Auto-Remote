namespace Markwardt.AutoRemote;

/// <summary>
/// What started a piece of work.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivitySource
{
    /// <summary>
    /// A message sent to the AI.
    /// </summary>
    Send,

    /// <summary>
    /// A queued input.
    /// </summary>
    Queue,

    /// <summary>
    /// A run of a loop.
    /// </summary>
    Loop,

    /// <summary>
    /// Bringing in the work of side sessions or delegated tasks.
    /// </summary>
    BringIn,

    /// <summary>
    /// The results of tasks the AI delegated.
    /// </summary>
    DelegatedResults
}
