namespace Markwardt.AutoRemote;

/// <summary>
/// A chat input a user set aside in a workspace to use later, which every client of the server sees.
/// </summary>
public sealed record SavedInput
{
    /// <summary>
    /// Gets the identifier the entry is taken by.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the text that was in the chat input, empty while the entry is being converted from speech or when that failed.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets when the entry was saved, or the recording of the speech it is made from ended.
    /// </summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// Gets a value indicating whether the entry is a recording the server is still turning into text, which fills <see cref="Text" /> when done and makes it a saved input like any other. Omitted when <see langword="false" />.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsConverting { get; init; }

    /// <summary>
    /// Gets whether the changes are committed after the work the input starts, which the input takes with it into the message box and into the queue. On unless it was turned off.
    /// </summary>
    public bool AutoCommit { get; init; } = true;

    /// <summary>
    /// Gets whether the input is queued: the server sends it by itself once the work of the workspace is over, and commits the changes after it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsQueued { get; init; }

    /// <summary>
    /// Gets whether the queued input is paused, since work was stopped, was cancelled or failed: it stays queued and is not sent until a user resumes the queue, work goes well again or another input is queued.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPaused { get; init; }

    /// <summary>
    /// Gets why a recording could not be turned into text, or <see langword="null" /> for every other entry. The entry only goes away when discarded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether the entry is a text that can be taken, which is every entry that is not a recording still being converted or one that failed to be.
    /// </summary>
    [JsonIgnore]
    public bool IsReady => !IsConverting && Error is null;

    /// <summary>
    /// Gets a value indicating whether the conversion of the entry from speech failed.
    /// </summary>
    [JsonIgnore]
    public bool HasError => Error is not null;

    /// <summary>
    /// Gets what the auto commit button of a listed input says it does.
    /// </summary>
    [JsonIgnore]
    public string AutoCommitTip => AutoCommit ? "Auto commit is on for this input. Press to turn it off." : "Auto commit is off for this input. Press to turn it on.";

    /// <summary>
    /// Gets what a list says about a queued input.
    /// </summary>
    [JsonIgnore]
    public string QueueLabel => IsPaused ? "Queued, paused" : "Queued";
}
