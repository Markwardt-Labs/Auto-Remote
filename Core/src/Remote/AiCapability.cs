namespace Markwardt.AutoRemote;

/// <summary>
/// How capable the model of a session should be, from which the library picks the model.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiCapability
{
    /// <summary>
    /// The quickest and cheapest model.
    /// </summary>
    Fast,

    /// <summary>
    /// A model between speed and strength.
    /// </summary>
    Balanced,

    /// <summary>
    /// A strong model.
    /// </summary>
    Strong,

    /// <summary>
    /// The strongest model.
    /// </summary>
    Max
}
