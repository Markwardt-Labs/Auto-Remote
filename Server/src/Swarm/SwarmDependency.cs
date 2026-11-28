namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The result of a finished task that another task builds on, kept with that task so it no longer needs the finished one. The server never merges it: the AI of the dependent task gets it beside its code and combines the two.
/// </summary>
internal sealed record SwarmDependency
{
    /// <summary>
    /// Gets the identifier of the finished task.
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// Gets its title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the commit it started from, which its changes are measured against.
    /// </summary>
    public required string BaseCommit { get; init; }

    /// <summary>
    /// Gets the commit holding its changes, or <see langword="null" /> when it changed no files.
    /// </summary>
    public string? ResultCommit { get; init; }

    /// <summary>
    /// Gets its report.
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// Gets the tasks whose changes its result was meant to include already.
    /// </summary>
    public IReadOnlyList<SwarmIncludedTask> Includes { get; init; } = [];
}

/// <summary>
/// What the changes of dependencies mean for the task that builds on them.
/// </summary>
internal static class SwarmDependencyExtensions
{
    extension(IReadOnlyList<SwarmDependency> dependencies)
    {
        /// <summary>
        /// Gets a value indicating whether there are changes to combine, which only a worker that knows how can be given.
        /// </summary>
        public bool HasChanges => dependencies.Any(dependency => dependency.ResultCommit is not null);

        /// <summary>
        /// Lists the tasks whose changes the result of the dependent task is meant to include.
        /// </summary>
        /// <returns>The tasks, each once.</returns>
        public IReadOnlyList<SwarmIncludedTask> Included() => [.. dependencies.Where(dependency => dependency.ResultCommit is not null).SelectMany(dependency => (IEnumerable<SwarmIncludedTask>)[.. dependency.Includes, new SwarmIncludedTask { TaskId = dependency.TaskId, Title = dependency.Title }]).DistinctBy(task => task.TaskId)];
    }
}
