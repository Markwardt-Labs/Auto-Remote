namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A sealed package ready to be delivered.
/// </summary>
/// <param name="Name">The name it is stored under on a drive, which sorts in the order to take them.</param>
/// <param name="Data">The sealed bytes.</param>
internal sealed record SwarmParcel(string Name, ReadOnlyMemory<byte> Data);
