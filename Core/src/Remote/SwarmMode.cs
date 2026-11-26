namespace Markwardt.AutoRemote;

/// <summary>
/// How work and its results travel between two paired servers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SwarmMode
{
    /// <summary>
    /// The servers connect to each other, and the code travels as branches under <c>swarm/</c> on the remote of the project, which both can reach.
    /// </summary>
    GitRemote,

    /// <summary>
    /// The servers connect to each other, and the code travels with the tasks as git bundles.
    /// </summary>
    Direct,

    /// <summary>
    /// The servers never connect: signed and encrypted packages, with the code as git bundles, are carried on a drive.
    /// </summary>
    AirGapped
}
