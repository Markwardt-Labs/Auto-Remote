namespace Markwardt.AutoRemote;

/// <summary>
/// What the server's computer has and how much of it each workspace takes, at the moment it was read.
/// </summary>
public sealed record ResourceReport
{
    /// <summary>
    /// Gets whether the server can measure resources, which it only can on Linux. Everything else is empty when it cannot.
    /// </summary>
    public required bool Supported { get; init; }

    /// <summary>
    /// Gets the memory and processor of the whole computer, or <see langword="null" /> when the server cannot measure them.
    /// </summary>
    public MachineResources? Machine { get; init; }

    /// <summary>
    /// Gets the workspaces that are working or still have processes running, the one using the most memory first.
    /// </summary>
    public IReadOnlyList<WorkspaceResources> Workspaces { get; init; } = [];

    /// <summary>
    /// Gets what the server's own process takes.
    /// </summary>
    public ResourceUsage Server { get; init; } = new();

    /// <summary>
    /// Gets what the processes the server started for no particular workspace take, such as a usage check or a local model server.
    /// </summary>
    public ResourceUsage Other { get; init; } = new();
}
