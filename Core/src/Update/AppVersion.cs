namespace Markwardt.AutoRemote;

/// <summary>
/// A release version in the <c>major.minor.patch</c> form with an optional pre-release suffix, ordered the way releases follow each other.
/// </summary>
public sealed record AppVersion : IComparable<AppVersion>
{
    private static readonly Regex pattern = new(@"^v?(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(?:-(?<suffix>[0-9A-Za-z][0-9A-Za-z.-]*))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Tells whether the left version is a later release than the right one.
    /// </summary>
    /// <param name="left">The version to test.</param>
    /// <param name="right">The version to compare with.</param>
    /// <returns><see langword="true" /> if the left one is later.</returns>
    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    /// <summary>
    /// Tells whether the left version is an earlier release than the right one.
    /// </summary>
    /// <param name="left">The version to test.</param>
    /// <param name="right">The version to compare with.</param>
    /// <returns><see langword="true" /> if the left one is earlier.</returns>
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    /// <summary>
    /// Reads a version such as <c>1.2.3</c> or <c>1.2.3-beta.1</c>, with an optional leading <c>v</c>.
    /// </summary>
    /// <param name="text">The text to read.</param>
    /// <param name="version">The version when the text is one.</param>
    /// <returns><see langword="true" /> if the text is a version.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;

        if (text is null || pattern.Match(text.Trim()) is not { Success: true } match || !int.TryParse(match.Groups["major"].Value, CultureInfo.InvariantCulture, out int major) || !int.TryParse(match.Groups["minor"].Value, CultureInfo.InvariantCulture, out int minor) || !int.TryParse(match.Groups["patch"].Value, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        version = new AppVersion { Major = major, Minor = minor, Patch = patch, Suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value : null };
        return true;
    }

    /// <summary>
    /// Gets the first number.
    /// </summary>
    public required int Major { get; init; }

    /// <summary>
    /// Gets the second number.
    /// </summary>
    public required int Minor { get; init; }

    /// <summary>
    /// Gets the third number.
    /// </summary>
    public required int Patch { get; init; }

    /// <summary>
    /// Gets the pre-release suffix after the hyphen, or <see langword="null" /> for a final release.
    /// </summary>
    public string? Suffix { get; init; }

    /// <inheritdoc />
    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int result = Major.CompareTo(other.Major);

        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        if (result != 0)
        {
            return result;
        }

        return (Suffix, other.Suffix) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (string own, string theirs) => string.CompareOrdinal(own, theirs),
        };
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Suffix is null ? string.Empty : "-" + Suffix)}";
}
