namespace Markwardt.AutoRemote;

/// <summary>
/// The size of the context window, in tokens, that a local model runs with for each capability; a capability left out uses the server's own size.
/// </summary>
public sealed record CapabilityContexts
{
    /// <summary>
    /// Gets the size for <see cref="AiCapability.Fast" />.
    /// </summary>
    public int? Fast { get; init; }

    /// <summary>
    /// Gets the size for <see cref="AiCapability.Balanced" />.
    /// </summary>
    public int? Balanced { get; init; }

    /// <summary>
    /// Gets the size for <see cref="AiCapability.Strong" />.
    /// </summary>
    public int? Strong { get; init; }

    /// <summary>
    /// Gets the size for <see cref="AiCapability.Max" />.
    /// </summary>
    public int? Max { get; init; }
}
