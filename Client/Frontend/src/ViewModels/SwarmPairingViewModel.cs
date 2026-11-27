namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A pairing found on the drive, with the button that confirms it after the fingerprints were compared.
/// </summary>
/// <param name="pairing">The pairing found on the drive.</param>
/// <param name="confirm">Pairs with the server once the user confirms.</param>
internal sealed class SwarmPairingViewModel(SwarmDrivePairing pairing, Func<SwarmDrivePairing, Task> confirm)
{
    /// <summary>
    /// Gets the pairing the row stands for.
    /// </summary>
    public SwarmDrivePairing Pairing => pairing;

    /// <summary>
    /// Gets what the pairing is.
    /// </summary>
    public string Title => pairing.Kind == SwarmPairingKind.Invitation ? $"{pairing.Name} invites this server to pair" : $"{pairing.Name} accepted your invitation";

    /// <summary>
    /// Gets the instruction to compare the fingerprint.
    /// </summary>
    public string Fingerprint => $"Pair only if its fingerprint is {pairing.Fingerprint}.";

    /// <summary>
    /// Gets the command that pairs with the server.
    /// </summary>
    public AsyncCommand ConfirmCommand { get; } = new(_ => confirm(pairing));
}
