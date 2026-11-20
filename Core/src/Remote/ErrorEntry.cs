namespace Markwardt.AutoRemote;

/// <summary>
/// An error the AI of a workspace ended with, kept by the server so that it can be read again after the chat has moved on.
/// </summary>
public sealed record ErrorEntry
{
    /// <summary>
    /// Gets when the error happened.
    /// </summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>
    /// Gets the message of the error.
    /// </summary>
    public required string Message { get; init; }
}
