namespace Markwardt.AutoRemote;

/// <summary>
/// A file attached to a release.
/// </summary>
public sealed record ReleaseAsset
{
    /// <summary>
    /// Gets the file name, such as <c>AutoRemote-linux</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the address the file is downloaded from.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Gets the SHA-256 of the file as lowercase hexadecimal, or <see langword="null" /> when the release does not state it.
    /// </summary>
    public string? Sha256 { get; init; }
}
