namespace Markwardt.AutoRemote;

/// <summary>
/// A task to delegate.
/// </summary>
public sealed record SwarmTaskRequest
{
    /// <summary>
    /// Gets a short title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets what the worker is to do, which must make sense on its own: the worker sees nothing of any conversation here.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// Gets what the result is expected to be, such as how to check it.
    /// </summary>
    public string? ExpectedResult { get; init; }

    /// <summary>
    /// Gets the capability the task needs.
    /// </summary>
    public AiCapability Capability { get; init; } = AiCapability.Balanced;

    /// <summary>
    /// Gets the paired server that is to do it, or <see langword="null" /> for any networked one.
    /// </summary>
    public string? PeerId { get; init; }

    /// <summary>
    /// Gets the tasks whose results this one builds on.
    /// </summary>
    public IReadOnlyList<Guid> DependsOn { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the result is brought into the workspace by itself once the task is done.
    /// </summary>
    public bool BringIn { get; init; } = true;
}
