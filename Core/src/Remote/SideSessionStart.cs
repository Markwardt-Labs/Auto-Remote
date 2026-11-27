namespace Markwardt.AutoRemote;

/// <summary>
/// What a side session's files start from.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SideSessionStart
{
    /// <summary>
    /// The commit main had checked out, without its changes that are not committed.
    /// </summary>
    Commit,

    /// <summary>
    /// Main's files as they were, changes that are not committed and new files included, ignored files left out.
    /// </summary>
    Files
}
