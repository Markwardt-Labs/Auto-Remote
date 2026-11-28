namespace Markwardt.AutoRemote.Client;

/// <summary>
/// An entry of the activity of a workspace as the Activity tab shows it.
/// </summary>
/// <param name="Entry">The entry.</param>
/// <param name="IsExpanded">Whether everything about it shows.</param>
internal sealed record ActivityEntryViewModel(ActivityEntry Entry, bool IsExpanded = false)
{
    /// <summary>
    /// Gets how it went, in a word.
    /// </summary>
    public string Outcome => Entry.Outcome.ToString();

    /// <summary>
    /// Gets the line naming it.
    /// </summary>
    public string Title => Entry.Title;

    /// <summary>
    /// Gets when it started.
    /// </summary>
    public string Time => Entry.StartedAt.ToStamp(DateTimeOffset.UtcNow);

    /// <summary>
    /// Gets a value indicating whether there is something for the user to look at.
    /// </summary>
    public bool NeedsAttention => Entry.NeedsAttention;

    /// <summary>
    /// Gets a value indicating whether it did not end well.
    /// </summary>
    public bool IsBad => Entry.Outcome is ActivityOutcome.Unfinished or ActivityOutcome.Failed;

    /// <summary>
    /// Gets what the AI was asked.
    /// </summary>
    public string Text => Entry.Text;

    /// <summary>
    /// Gets a value indicating whether what the AI was asked is known.
    /// </summary>
    public bool HasText => Entry.Text.Length > 0;

    /// <summary>
    /// Gets why it did not end well.
    /// </summary>
    public string? Reason => Entry.Reason;

    /// <summary>
    /// Gets a value indicating whether there is a reason.
    /// </summary>
    public bool HasReason => !string.IsNullOrEmpty(Entry.Reason);

    /// <summary>
    /// Gets the last reply of the AI.
    /// </summary>
    public string? Report => Entry.Report;

    /// <summary>
    /// Gets a value indicating whether the AI replied.
    /// </summary>
    public bool HasReport => !string.IsNullOrEmpty(Entry.Report);

    /// <summary>
    /// Gets what came of committing: the commit and whether it was pushed, and why either failed.
    /// </summary>
    public string CommitText => string.Join('\n', new[]
    {
        Entry.Commit is { } commit ? $"{commit}{Entry.Pushed switch { true => " (pushed)", false => " (not pushed)", null => string.Empty }}" : null,
        Entry.CommitError is { } error ? $"{(Entry.Commit is null ? "Commit failed" : "Push failed")}: {error}" : null
    }.Where(line => line is not null));

    /// <summary>
    /// Gets a value indicating whether anything came of committing.
    /// </summary>
    public bool HasCommit => CommitText.Length > 0;

    /// <summary>
    /// Gets the files of what was brought in, one per line.
    /// </summary>
    public string Files => string.Join('\n', Entry.Files);

    /// <summary>
    /// Gets a value indicating whether there are files.
    /// </summary>
    public bool HasFiles => Entry.Files.Count > 0;

    /// <summary>
    /// Gets what was brought in said about its work.
    /// </summary>
    public string? Handoff => Entry.Handoff;

    /// <summary>
    /// Gets a value indicating whether there is a handoff.
    /// </summary>
    public bool HasHandoff => !string.IsNullOrEmpty(Entry.Handoff);

    /// <summary>
    /// Gets what else happened to it, one per line.
    /// </summary>
    public string Notes => string.Join('\n', Entry.Notes);

    /// <summary>
    /// Gets a value indicating whether there are notes.
    /// </summary>
    public bool HasNotes => Entry.Notes.Count > 0;
}
