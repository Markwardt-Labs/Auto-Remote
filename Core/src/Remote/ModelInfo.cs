namespace Markwardt.AutoRemote;

/// <summary>
/// An AI model that an <see cref="AiService" /> can run, with the settings it offers.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>
    /// Gets the name the service takes for the model, which is what <see cref="RemoteRequest.Model" /> and <see cref="WorkspaceAi.Model" /> hold.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the name to show for the model, or <see langword="null" /> to show <see cref="Id" />.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets what the service says about the model, or <see langword="null" /> when it says nothing.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether the model takes images as input, or <see langword="null" /> when the service does not say.
    /// </summary>
    public bool? AcceptsImages { get; init; }

    /// <summary>
    /// Gets the tokens the context window holds as the service runs the model, or <see langword="null" /> when not known.
    /// </summary>
    public int? ContextLimit { get; init; }

    /// <summary>
    /// Gets the settings the model offers; empty for a model that has none.
    /// </summary>
    public IReadOnlyList<ModelSettingInfo> Settings { get; init; } = [];
}
