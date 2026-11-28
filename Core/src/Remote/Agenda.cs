namespace Markwardt.AutoRemote;

/// <summary>
/// What a workspace is going to run by itself, in order, and why it waits.
/// </summary>
public sealed record Agenda
{
    /// <summary>
    /// Gets the items in the order they run.
    /// </summary>
    public IReadOnlyList<AgendaItem> Items { get; init; } = [];

    /// <summary>
    /// Gets the one line that says what the workspace waits for, or <see langword="null" /> when there is nothing to explain.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user stopped the work that runs by itself, which holds it all until it is resumed.
    /// </summary>
    public bool IsStopped { get; init; }

    /// <summary>
    /// Gets a value indicating whether more work is to come without anyone asking for it, not counting the runs of a loop.
    /// </summary>
    [JsonIgnore]
    public bool IsPending => !IsStopped && Items.Any(item => item.Kind != AgendaKind.Loop);
}
