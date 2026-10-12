namespace Markwardt.AutoRemote;

/// <summary>
/// How much effort the AI puts into each of a workspace's prompts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiEffort
{
    /// <summary>
    /// The least effort.
    /// </summary>
    Low,

    /// <summary>
    /// Moderate effort.
    /// </summary>
    Medium,

    /// <summary>
    /// The default effort.
    /// </summary>
    High,

    /// <summary>
    /// Extra high effort.
    /// </summary>
    XHigh,

    /// <summary>
    /// The most effort.
    /// </summary>
    Max
}
