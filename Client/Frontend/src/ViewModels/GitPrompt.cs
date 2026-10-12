namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The shape of the question a workspace's git controls are asking.
/// </summary>
internal enum GitPrompt
{
    /// <summary>
    /// Nothing is being asked.
    /// </summary>
    None,

    /// <summary>
    /// A text is asked for, such as a commit message or a name.
    /// </summary>
    Input,

    /// <summary>
    /// A yes or no is asked, possibly for something that destroys work.
    /// </summary>
    Confirm,

    /// <summary>
    /// The user is told something and acknowledges it.
    /// </summary>
    Notice,

    /// <summary>
    /// The name and email git commits with are asked for.
    /// </summary>
    Identity
}
