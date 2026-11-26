namespace Markwardt.AutoRemote;

/// <summary>
/// How the server runs the work of its local AI when more than one workspace has work. Work of the other providers never waits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalWorkMode
{
    /// <summary>
    /// Only one local AI session works at a time: other work waits in a queue, in which the work of this server's user comes before delegated work.
    /// </summary>
    Yield,

    /// <summary>
    /// Work runs side by side; only the number of delegated tasks working at once is limited.
    /// </summary>
    Simultaneous
}
