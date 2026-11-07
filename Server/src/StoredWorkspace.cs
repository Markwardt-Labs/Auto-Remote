namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the server keeps of a workspace in its workspaces file: who it is, which AI session holds its folder, provider and configuration, the group it is filed in and the review and expand prompts it has of its own. Everything else about a workspace is read from its session.
/// </summary>
internal sealed record StoredWorkspace
{
    /// <summary>
    /// Gets the identifier of the workspace, which stays the same when its session is replaced.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the identifier of the live AI session of the workspace (32 hexadecimal digits), or <see langword="null" /> for a workspace an older release stored without a usable session, which the server gives a new one when it starts.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the group the workspace is filed in, or <see langword="null" /> when it is not in one.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Gets the review prompt of the workspace itself, or <see langword="null" /> when it uses the one of the environment.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewPrompt { get; init; }

    /// <summary>
    /// Gets the expand prompt of the workspace itself, or <see langword="null" /> when it uses the one of the environment.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpandPrompt { get; init; }

    /// <summary>
    /// Gets a value indicating whether the session was stored by an older release, before sessions kept the settings the server runs them with, and still has to be given them (<see cref="IPrompterHost.Adopt" />), which the server does when it starts and then clears this.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Adopt { get; init; }

    /// <summary>
    /// Gets the folder of a workspace that has no session yet, so that its first one can be made; it is never stored for a workspace that has one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; init; }

    /// <summary>
    /// Gets the provider of a workspace that has no session yet, so that its first one can be made; it is never stored for a workspace that has one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiService? Service { get; init; }
}
