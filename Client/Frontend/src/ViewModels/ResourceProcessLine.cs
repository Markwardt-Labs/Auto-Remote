namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A process listed in a resources section.
/// </summary>
internal sealed record ResourceProcessLine
{
    /// <summary>
    /// Gets the command line of the process.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Gets what it takes.
    /// </summary>
    public required string Usage { get; init; }
}
