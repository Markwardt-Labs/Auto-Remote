namespace Markwardt.AutoRemote;

/// <summary>
/// A setting that one AI model offers, such as the reasoning effort it runs at. Values are always text.
/// </summary>
public sealed record ModelSettingInfo
{
    /// <summary>
    /// Gets the identifier of the setting, which is the key of its value in <see cref="WorkspaceAi.ModelSettings" />.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the setting.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what the setting does, or <see langword="null" /> when there is nothing to add to its name.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets what kind of value the setting takes.
    /// </summary>
    public required ModelSettingKind Kind { get; init; }

    /// <summary>
    /// Gets the values of a <see cref="ModelSettingKind.Choice" /> setting, from the lowest to the highest; empty for the other kinds.
    /// </summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// Gets the smallest value of a <see cref="ModelSettingKind.Number" /> setting.
    /// </summary>
    public int? Minimum { get; init; }

    /// <summary>
    /// Gets the largest value of a <see cref="ModelSettingKind.Number" /> setting.
    /// </summary>
    public int? Maximum { get; init; }

    /// <summary>
    /// Gets the value used until another is chosen.
    /// </summary>
    public required string Default { get; init; }

    /// <summary>
    /// Tells whether a value is one this setting takes.
    /// </summary>
    /// <param name="value">The value, as text.</param>
    /// <returns>Whether the value is a choice of the setting, <c>true</c> or <c>false</c> for a toggle, or a whole number in range.</returns>
    public bool Accepts(string value) => Kind switch
    {
        ModelSettingKind.Choice => Choices.Contains(value),
        ModelSettingKind.Toggle => value is "true" or "false",
        ModelSettingKind.Number => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= (Minimum ?? int.MinValue) && number <= (Maximum ?? int.MaxValue),
        _ => false
    };
}
