namespace Markwardt.AutoRemote;

/// <summary>
/// Something a workspace is going to run or do by itself.
/// </summary>
public sealed record AgendaItem
{
    /// <summary>
    /// Gets what it is.
    /// </summary>
    public required AgendaKind Kind { get; init; }

    /// <summary>
    /// Gets what it is about: the saved input of a queued item, or what a bring-in brings in.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>
    /// Gets what a list says about it.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets why it does not run yet, or <see langword="null" /> when only what is before it is in its way.
    /// </summary>
    public string? Waiting { get; init; }

    /// <summary>
    /// Gets whether the changes are committed after it, for items where that can be chosen.
    /// </summary>
    public bool? AutoCommit { get; init; }

    /// <summary>
    /// Gets when it is due, for items that wait for a time.
    /// </summary>
    public DateTimeOffset? At { get; init; }
}
