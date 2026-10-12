namespace Markwardt.AutoRemote;

/// <summary>
/// The AI model a workspace's prompts run on; which ones exist depends on the <see cref="AiService" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiModel
{
    /// <summary>
    /// The balanced default model.
    /// </summary>
    Sonnet,

    /// <summary>
    /// The most capable model.
    /// </summary>
    Opus,

    /// <summary>
    /// The fastest model.
    /// </summary>
    Haiku,

    /// <summary>
    /// The balanced Codex model.
    /// </summary>
    Terra,

    /// <summary>
    /// The fast Codex model.
    /// </summary>
    Sol,

    /// <summary>
    /// The lightweight Codex model.
    /// </summary>
    Luna
}
