namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Remembers which sessions had their AI run which shell commands, so a process running one can be told to belong to the session.
/// </summary>
internal interface IShellCommandLog
{
    /// <summary>
    /// Remembers that the AI of a session runs a command.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="command">The command as it is given to the shell.</param>
    void Note(Guid id, string command);

    /// <summary>
    /// Finds the sessions that lately ran a command.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>The sessions, the one that ran it last first.</returns>
    IReadOnlyList<Guid> Find(string command);
}

/// <inheritdoc />
internal sealed class ShellCommandLog : IShellCommandLog
{
    private readonly Lock sync = new();

    private readonly List<(Guid Id, string Command)> commands = [];

    private readonly int capacity = 500;

    /// <inheritdoc />
    public void Note(Guid id, string command)
    {
        lock (sync)
        {
            commands.Remove((id, command));
            commands.Add((id, command));

            if (commands.Count > capacity)
            {
                commands.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> Find(string command)
    {
        lock (sync)
        {
            return [.. commands.Where(item => item.Command == command).Select(item => item.Id).Reverse()];
        }
    }
}
