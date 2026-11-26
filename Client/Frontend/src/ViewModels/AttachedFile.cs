namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A file a user message refers to with an <c>Attached file: path</c> line.
/// </summary>
internal sealed record AttachedFile
{
    /// <summary>
    /// Gets the file name as the user attached it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the file's path on the server.
    /// </summary>
    public required string Path { get; init; }
}
