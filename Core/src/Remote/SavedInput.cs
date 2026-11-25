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
}
