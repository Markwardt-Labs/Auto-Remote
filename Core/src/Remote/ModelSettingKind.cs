namespace Markwardt.AutoRemote;

/// <summary>
/// What kind of value a <see cref="ModelSettingInfo" /> takes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelSettingKind
{
    /// <summary>
    /// One of a list of values, ordered from the lowest to the highest, such as a reasoning effort.
    /// </summary>
    Choice,

    /// <summary>
    /// On or off, written <c>true</c> or <c>false</c>.
    /// </summary>
    Toggle,

    /// <summary>
    /// A whole number within a range.
    /// </summary>
    Number
}
