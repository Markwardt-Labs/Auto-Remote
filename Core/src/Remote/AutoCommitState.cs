namespace Markwardt.AutoRemote;

/// <summary>
/// Whether the changes of a workspace are committed by themselves once the AI's work is over.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoCommitState
{
    /// <summary>
    /// Nothing is committed when the work is over.
    /// </summary>
    Off,

    /// <summary>
    /// The changes are committed once the work is over and went well.
    /// </summary>
    On,

    /// <summary>
    /// The work is over and its changes are being committed, which no other work starts before.
    /// </summary>
    Committing
}
