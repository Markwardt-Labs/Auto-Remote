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

/// <summary>
/// A user message split into what the user typed and the files attached to it.
/// </summary>
internal sealed record MessageParts
{
    /// <summary>
    /// Gets the text the user typed, without the attachment lines.
    /// </summary>
    public required string Body { get; init; }

    /// <summary>
    /// Gets the attached files, in the order they were sent.
    /// </summary>
    public required IReadOnlyList<AttachedFile> Files { get; init; }

    /// <summary>
    /// Splits a prompt into its text and the trailing <c>Attached file: path</c> lines the clients add for each attachment.
    /// </summary>
    /// <param name="text">The stored prompt.</param>
    /// <returns>The parts of the prompt.</returns>
    public static MessageParts Parse(string text)
    {
        string prefix = "Attached file: ";
        List<string> lines = [.. text.Split('\n').Select(line => line.TrimEnd('\r'))];
        List<AttachedFile> files = [];

        while (lines.Count > 0 && lines[^1].StartsWith(prefix, StringComparison.Ordinal) && lines[^1].Length > prefix.Length)
        {
            string path = lines[^1][prefix.Length..];
            string name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            files.Insert(0, new AttachedFile { Name = Regex.Replace(name, "^[0-9a-f]{8}-(?=.)", string.Empty), Path = path });
            lines.RemoveAt(lines.Count - 1);
        }

        return new MessageParts { Body = string.Join('\n', lines).TrimEnd(), Files = files };
    }
}
