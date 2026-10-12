namespace Markwardt.AutoRemote;

/// <summary>
/// The server understood a request but refused or failed it.
/// </summary>
/// <param name="message">The server's explanation.</param>
public sealed class RemoteException(string message) : Exception(message);
