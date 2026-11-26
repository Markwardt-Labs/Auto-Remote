namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Tasks delegated together by one AI, which is told once all of them ended.
/// </summary>
internal sealed class SwarmJob
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the workspace.
    /// </summary>
    public required Guid WorkspaceId { get; init; }

    /// <summary>
    /// Gets or sets whether the user delegated it, so no AI is told.
    /// </summary>
    public bool IsManual { get; set; }

    /// <summary>
    /// Gets or sets whether the AI was told that the tasks ended.
    /// </summary>
    public bool IsReported { get; set; }

    /// <summary>
    /// Gets or sets when it was made.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets how often the job needed reporting again, so a report made before the latest change does not count for it.
    /// </summary>
    public int Revision { get; set; }
}
