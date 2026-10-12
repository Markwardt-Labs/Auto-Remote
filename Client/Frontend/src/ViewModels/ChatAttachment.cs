namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A file waiting in the message box to be sent with the next prompt.
/// </summary>
internal sealed record ChatAttachment
{
    /// <summary>
    /// Gets the file name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the file content.
    /// </summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}
