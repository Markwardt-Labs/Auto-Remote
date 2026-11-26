namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A delegated task as the list of the swarm screen shows it.
/// </summary>
internal sealed record SwarmTaskViewModel(SwarmTaskInfo Task)
{
    /// <summary>
    /// Gets the title, with an arrow showing its direction.
    /// </summary>
    public string Title => $"{(Task.IsIncoming ? "←" : "→")} {Task.Title}";

    /// <summary>
    /// Gets the line under the title: the state, the other server and the workspace.
    /// </summary>
    public string Detail => Describe(Task);

    /// <summary>
    /// Says where a task stands, with whom.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>The text.</returns>
    public string Describe(SwarmTaskInfo task)
    {
        List<string> parts = [State(task)];

        if (task.PeerName.Length > 0)
        {
            parts.Add($"{(task.IsIncoming ? "from" : "on")} {task.PeerName}");
        }

        if (task.WorkspaceName.Length > 0 && !task.IsIncoming)
        {
            parts.Add(task.WorkspaceName);
        }

        return string.Join(" - ", parts);
    }

    private string State(SwarmTaskInfo task) => task.State switch
    {
        SwarmTaskState.InTransit => "In transit",
        SwarmTaskState.Pending => "Waiting for a worker",
        SwarmTaskState.Waiting => "Waiting for other tasks",
        SwarmTaskState.Done when task.IsApplied => "Done, applied",
        _ => task.State.ToString()
    };
}
