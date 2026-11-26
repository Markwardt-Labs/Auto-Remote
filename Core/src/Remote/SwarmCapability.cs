namespace Markwardt.AutoRemote;

/// <summary>
/// A capability a worker offers, with the model that serves it.
/// </summary>
public sealed record SwarmCapability
{
    /// <summary>
    /// Gets the capability.
    /// </summary>
    public required AiCapability Capability { get; init; }

    /// <summary>
    /// Gets the local model of the worker for it.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Gets the context size in tokens the worker gives the model, when it sets one.
    /// </summary>
    public int? Context { get; init; }
}
