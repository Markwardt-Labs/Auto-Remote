namespace Markwardt.AutoRemote;

/// <summary>
/// A published release and the files attached to it.
/// </summary>
public sealed record ReleaseInfo
{
    /// <summary>
    /// Gets the version the release carries, which is its tag.
    /// </summary>
    public required AppVersion Version { get; init; }

    /// <summary>
    /// Gets the files of the release.
    /// </summary>
    public required IReadOnlyList<ReleaseAsset> Assets { get; init; }
}
