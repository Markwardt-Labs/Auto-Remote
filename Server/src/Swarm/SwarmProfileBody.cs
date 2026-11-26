namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of a profile: what a server can do for the receiver.
/// </summary>
internal sealed record SwarmProfileBody
{
    /// <summary>
    /// Gets the name of the sender.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets whether the sender takes tasks from the receiver now.
    /// </summary>
    public bool AcceptsWork { get; init; }

    /// <summary>
    /// Gets how many more tasks the sender would take at once.
    /// </summary>
    public int FreeSlots { get; init; }

    /// <summary>
    /// Gets the capabilities it offers.
    /// </summary>
    public IReadOnlyList<SwarmCapability> Capabilities { get; init; } = [];

    /// <summary>
    /// Gets the capabilities whose model is loaded right now.
    /// </summary>
    public IReadOnlyList<AiCapability> LoadedCapabilities { get; init; } = [];

    /// <summary>
    /// Gets what the AI of a task of the receiver may do on the sender.
    /// </summary>
    public SwarmPermissions Permissions { get; init; } = new();
}
