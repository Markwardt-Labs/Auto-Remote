namespace Markwardt.AutoRemote;

/// <summary>
/// How hard the model of a session should work.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiEffort
{
    /// <summary>
    /// The least effort.
    /// </summary>
    Low,

    /// <summary>
    /// A moderate effort.
    /// </summary>
    Medium,

    /// <summary>
    /// A high effort.
    /// </summary>
    High,

    /// <summary>
    /// The highest effort.
    /// </summary>
    Max
}
