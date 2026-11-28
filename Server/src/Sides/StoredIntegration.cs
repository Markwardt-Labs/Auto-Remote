namespace Markwardt.AutoRemote.Server;

/// <summary>
/// An integration of a side session that is under way.
/// </summary>
internal sealed record StoredIntegration
{
    /// <summary>
    /// Gets the identifier the main AI finishes it with, new for every time an integration is started.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the folder with the change list, the diff and the handoff, once they are made.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>
    /// Gets the text of the request to the main AI, once it is made.
    /// </summary>
    public string? Request { get; init; }

    /// <summary>
    /// Gets how many times the request was given to the main AI.
    /// </summary>
    public int Deliveries { get; init; }

    /// <summary>
    /// Gets a value indicating whether the main AI has the request in the work it is doing now.
    /// </summary>
    public bool InWork { get; init; }

    /// <summary>
    /// Gets the identifier of the work that brings it in, which every attempt shares, so they make one entry of activity.
    /// </summary>
    public Guid StepId { get; init; }

    /// <summary>
    /// Gets how often in a row it was put off because the AI could not run, which makes it wait longer each time.
    /// </summary>
    public int Delays { get; init; }

    /// <summary>
    /// Gets a value indicating whether a restart of the server ended a turn it had.
    /// </summary>
    public bool Interrupted { get; init; }

    /// <summary>
    /// Gets what the session said about its work.
    /// </summary>
    public string Handoff { get; init; } = string.Empty;

    /// <summary>
    /// Gets the files the session changed.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}
