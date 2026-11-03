namespace Markwardt.AutoRemote;

/// <summary>
/// The model a workspace's AI session runs and the values of that model's settings, as the session stores them.
/// </summary>
public sealed record WorkspaceAi
{
    /// <summary>
    /// Gets the identifier of the model, one of those <see cref="RemoteCommand.ListModels" /> lists for the workspace's service, or <see langword="null" /> when the session has none: the model it was stored with is no longer offered, and one has to be chosen.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the values of the model's settings by <see cref="ModelSettingInfo.Id" />.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelSettings { get; init; } = new Dictionary<string, string>();
}
