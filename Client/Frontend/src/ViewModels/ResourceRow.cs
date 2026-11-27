namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A row of a resources section: what a workspace, a session or the server takes.
/// </summary>
internal sealed record ResourceRow
{
    /// <summary>
    /// Gets what the row is about.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what it is doing, empty when the row does not say.
    /// </summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// Gets what it takes.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Gets a value indicating whether the row is a part of the one above it.
    /// </summary>
    public bool IsPart { get; init; }

    /// <summary>
    /// Gets the processes that take the most memory or processor, none when the row does not list them.
    /// </summary>
    public IReadOnlyList<ResourceProcessLine> Processes { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether <see cref="State" /> says anything.
    /// </summary>
    public bool HasState => State.Length > 0;

    /// <summary>
    /// Gets the space left of the row.
    /// </summary>
    public Thickness Indent => new(IsPart ? 16 : 0, 0, 0, 0);
}
