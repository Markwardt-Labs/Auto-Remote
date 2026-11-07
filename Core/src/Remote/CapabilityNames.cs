namespace Markwardt.AutoRemote;

/// <summary>
/// A name for each capability, such as the local model to run for it.
/// </summary>
public sealed record CapabilityNames
{
    /// <summary>
    /// Gets the name for <see cref="AiCapability.Fast" />.
    /// </summary>
    public string Fast { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name for <see cref="AiCapability.Balanced" />.
    /// </summary>
    public string Balanced { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name for <see cref="AiCapability.Strong" />.
    /// </summary>
    public string Strong { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name for <see cref="AiCapability.Max" />.
    /// </summary>
    public string Max { get; init; } = string.Empty;
}
