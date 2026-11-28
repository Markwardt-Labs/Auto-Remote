namespace Markwardt.AutoRemote;

/// <summary>
/// The kinds of items of an <see cref="Agenda" />, in the order they go first.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgendaKind
{
    /// <summary>
    /// The changes of the work that ended are committed.
    /// </summary>
    Commit,

    /// <summary>
    /// Work that failed or was interrupted is tried again.
    /// </summary>
    Retry,

    /// <summary>
    /// The AI brings the results of other sessions or delegated tasks into the workspace.
    /// </summary>
    BringIn,

    /// <summary>
    /// The workspace waits for tasks its AI delegated.
    /// </summary>
    DelegatedResults,

    /// <summary>
    /// A queued input is sent.
    /// </summary>
    Queued,

    /// <summary>
    /// The loop makes its next run.
    /// </summary>
    Loop,

    /// <summary>
    /// Work waits for the local AI.
    /// </summary>
    LocalAi
}
