namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of a task package.
/// </summary>
internal sealed record SwarmTaskBody
{
    /// <summary>
    /// Gets the identifier of the task.
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// Gets the identifier of its job.
    /// </summary>
    public Guid JobId { get; init; }

    /// <summary>
    /// Gets its title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets what to do.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// Gets what the result is expected to be.
    /// </summary>
    public string ExpectedResult { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capability it needs.
    /// </summary>
    public AiCapability Capability { get; init; }

    /// <summary>
    /// Gets how its code travels.
    /// </summary>
    public SwarmMode Mode { get; init; }

    /// <summary>
    /// Gets the first commit of the repository, by which the worker recognizes it.
    /// </summary>
    public required string RepoKey { get; init; }

    /// <summary>
    /// Gets the commit the work starts from.
    /// </summary>
    public required string BaseCommit { get; init; }

    /// <summary>
    /// Gets the remote the code is fetched from in the git remote mode.
    /// </summary>
    public string? RemoteUrl { get; init; }
}
