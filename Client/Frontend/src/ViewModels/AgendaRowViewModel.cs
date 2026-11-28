namespace Markwardt.AutoRemote.Client;

/// <summary>
/// An item of the agenda as the Up Next list shows it.
/// </summary>
/// <param name="Item">The item.</param>
internal sealed record AgendaRowViewModel(AgendaItem Item)
{
    /// <summary>
    /// Gets what the list says about it.
    /// </summary>
    public string Title => Item.Title;

    /// <summary>
    /// Gets the line under the title: why it waits and when it is due, or an empty text.
    /// </summary>
    public string Detail => Describe(DateTimeOffset.UtcNow);

    /// <summary>
    /// Gets a value indicating whether there is a line under the title.
    /// </summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>
    /// Gets a value indicating whether it is a queued input, which can be edited, run in a new session and deleted.
    /// </summary>
    public bool IsQueued => Item is { Kind: AgendaKind.Queued, Id: not null };

    /// <summary>
    /// Gets a value indicating whether it can be chosen whether the changes are committed after it.
    /// </summary>
    public bool HasCommitChoice => Item is { Kind: AgendaKind.Queued or AgendaKind.BringIn, Id: not null, AutoCommit: not null };

    /// <summary>
    /// Gets a value indicating whether the changes are committed after it.
    /// </summary>
    public bool IsCommitOn => Item.AutoCommit == true;

    /// <summary>
    /// Gets what the commit button of the row says it does.
    /// </summary>
    public string CommitTip => IsCommitOn ? "The changes are committed after it. Press to keep them uncommitted." : "The changes are kept uncommitted after it. Press to commit them.";

    /// <summary>
    /// Writes the line under the title.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The text.</returns>
    public string Describe(DateTimeOffset now) => string.Join(" - ", new[] { Item.Waiting, Item.At is { } at ? $"at {at.ToStamp(now)}" : null }.Where(part => !string.IsNullOrEmpty(part)));
}
