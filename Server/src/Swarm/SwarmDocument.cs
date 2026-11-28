namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Everything the swarm stores.
/// </summary>
internal sealed class SwarmDocument
{
    /// <summary>
    /// Gets or sets the settings of this server.
    /// </summary>
    public SwarmServer Server { get; set; } = new();

    /// <summary>
    /// Gets or sets the paired servers.
    /// </summary>
    public List<SwarmPeerRecord> Peers { get; set; } = [];

    /// <summary>
    /// Gets or sets the jobs delegated from here.
    /// </summary>
    public List<SwarmJob> Jobs { get; set; } = [];

    /// <summary>
    /// Gets or sets the tasks delegated from here.
    /// </summary>
    public List<SwarmOutgoing> Outgoing { get; set; } = [];

    /// <summary>
    /// Gets or sets the turns that bring results of delegated tasks in and are not over.
    /// </summary>
    public List<SwarmTurn> Turns { get; set; } = [];

    /// <summary>
    /// Gets or sets the tasks delegated here.
    /// </summary>
    public List<SwarmIncoming> Incoming { get; set; } = [];

    /// <summary>
    /// Gets or sets the packages handled, so one arriving twice does nothing.
    /// </summary>
    public Dictionary<Guid, DateTimeOffset> Seen { get; set; } = [];

    /// <summary>
    /// Gets or sets when this server last wrote an invitation to a drive, from which on an acceptance of it can be confirmed.
    /// </summary>
    public DateTimeOffset? InvitedAt { get; set; }
}

/// <summary>
/// What the stored tasks of a workspace wait for.
/// </summary>
internal static class SwarmDocumentExtensions
{
    extension(SwarmDocument document)
    {
        /// <summary>
        /// Checks whether nothing more happens to any task of a job by itself: each ended, or waits on a task that was lost.
        /// </summary>
        /// <param name="jobId">The job.</param>
        /// <returns><see langword="true" /> if so; <see langword="false" /> for a job without tasks.</returns>
        public bool IsOver(Guid jobId)
        {
            List<SwarmOutgoing> tasks = [.. document.Outgoing.Where(task => task.JobId == jobId)];
            return tasks.Count > 0 && tasks.All(task => document.IsSettled(task, tasks));
        }

        /// <summary>
        /// Checks whether the result of a task is to be brought in now and has no turn for it yet. The result of a task an AI delegated waits for the rest of its job, unless the user asked for it.
        /// </summary>
        /// <param name="task">The task.</param>
        /// <returns><see langword="true" /> if it is.</returns>
        public bool IsToBringIn(SwarmOutgoing task)
            => task is { BringIn: true, State: SwarmTaskState.Done, ResultCommit: not null, TurnId: null }
                && (task.IsManual || task.IsAsked || document.Jobs.FirstOrDefault(job => job.Id == task.JobId) is not { IsReported: false } || document.IsOver(task.JobId));

        /// <summary>
        /// Lists the jobs of a workspace's AI that are over, which it has not been told and no turn is about to tell.
        /// </summary>
        /// <param name="workspaceId">The workspace.</param>
        /// <returns>The jobs.</returns>
        public IEnumerable<SwarmJob> DueReports(Guid workspaceId)
            => document.Jobs.Where(job => job.WorkspaceId == workspaceId && job is { IsManual: false, IsReported: false } && !document.Turns.Any(turn => turn.Jobs.ContainsKey(job.Id)) && document.IsOver(job.Id));

        // A task that waits on a lost one is settled too, since it only goes on if that one comes back.
        private bool IsSettled(SwarmOutgoing task, IReadOnlyList<SwarmOutgoing> job) => task.State is SwarmTaskState.Done or SwarmTaskState.Failed or SwarmTaskState.Cancelled or SwarmTaskState.Lost
            || task.State == SwarmTaskState.Waiting && task.DependsOn.Any(id => job.FirstOrDefault(other => other.Id == id) is { } dependency && (dependency.State == SwarmTaskState.Lost || dependency.State == SwarmTaskState.Waiting && document.IsSettled(dependency, job)));
    }
}
