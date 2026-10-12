namespace Markwardt.AutoRemote;

/// <summary>
/// The script and task runs the server holds for a workspace.
/// </summary>
public sealed record ScriptRuns
{
    /// <summary>
    /// Gets the runs, earliest started first, a task followed by the scripts it runs.
    /// </summary>
    public required IReadOnlyList<ScriptRun> Runs { get; init; }
}
