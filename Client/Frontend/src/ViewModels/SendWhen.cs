namespace Markwardt.AutoRemote.Client;

/// <summary>
/// When Send Options gives the work to the AI.
/// </summary>
internal enum SendWhen
{
    /// <summary>
    /// At once.
    /// </summary>
    Now,

    /// <summary>
    /// After the current work, as a queued input.
    /// </summary>
    Next,

    /// <summary>
    /// Over and over, as a loop.
    /// </summary>
    Repeat
}
