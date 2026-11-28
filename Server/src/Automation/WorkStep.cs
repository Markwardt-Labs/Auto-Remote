namespace Markwardt.AutoRemote.Server;

/// <summary>
/// One piece of work of a workspace, from what asks for it until it ended well, was given up or was stopped. It is kept on disk, so work a restart interrupts is gone on with.
/// </summary>
internal sealed record WorkStep
{
    /// <summary>
    /// Gets the identifier, which its activity entry shares.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets what started it.
    /// </summary>
    public required ActivitySource Source { get; init; }

    /// <summary>
    /// Gets what the AI is asked.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets whether it asks for its own changes to be committed, or <see langword="null" /> when it says nothing about that, as a sent message does.
    /// </summary>
    public bool? Commit { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user turned its commit off while it waited, which queuing does not undo.
    /// </summary>
    public bool CommitDeclined { get; init; }

    /// <summary>
    /// Gets a value indicating whether its commit was turned on only because something to commit was queued while it waited.
    /// </summary>
    public bool CommitFromQueue { get; init; }

    /// <summary>
    /// Gets what the AI is told before it takes the work up again, or <see langword="null" /> for work that is new to it.
    /// </summary>
    public string? Preface { get; init; }

    /// <summary>
    /// Gets how often it failed in a way that counts toward giving it up.
    /// </summary>
    public int Failures { get; init; }

    /// <summary>
    /// Gets how often it was put off because the AI could not run.
    /// </summary>
    public int Delays { get; init; }

    /// <summary>
    /// Gets how often the server stopped while it was under way.
    /// </summary>
    public int Interruptions { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI has taken it.
    /// </summary>
    public bool Started { get; init; }

    /// <summary>
    /// Gets a value indicating whether it waits for the usage limit to let the AI go on.
    /// </summary>
    public bool IsSuspended { get; init; }

    /// <summary>
    /// Gets why the AI said it could not finish it, or <see langword="null" />.
    /// </summary>
    public string? Unfinished { get; init; }

    /// <summary>
    /// Gets a value indicating whether nobody may be there while it is done.
    /// </summary>
    [JsonIgnore]
    public bool IsUnattended => Source != ActivitySource.Send || Preface is not null;

    /// <summary>
    /// Gets a short line naming it.
    /// </summary>
    [JsonIgnore]
    public string Title => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line ? line.Length > 120 ? $"{line[..120]}..." : line : "Work";
}
