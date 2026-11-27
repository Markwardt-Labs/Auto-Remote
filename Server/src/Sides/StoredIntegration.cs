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
    /// Gets a value indicating whether the request waits to go along with the next work of main rather than being sent by itself.
    /// </summary>
    public bool WaitsForWork { get; init; }
}
