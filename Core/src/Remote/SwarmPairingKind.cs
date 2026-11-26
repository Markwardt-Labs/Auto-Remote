namespace Markwardt.AutoRemote;

/// <summary>
/// What a pairing file on a drive asks to be confirmed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwarmPairingKind
{
    /// <summary>
    /// Another server invites this one to pair.
    /// </summary>
    Invitation,

    /// <summary>
    /// Another server accepted an invitation of this one.
    /// </summary>
    Acceptance
}
