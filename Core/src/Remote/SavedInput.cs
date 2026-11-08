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
    /// Gets the text that was in the chat input.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets when the entry was saved.
    /// </summary>
    public required DateTimeOffset Time { get; init; }
}
