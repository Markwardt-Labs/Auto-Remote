namespace Markwardt.AutoRemote;

/// <summary>
/// The server understood a request but refused or failed it.
/// </summary>
public sealed class RemoteException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteException" /> class.
    /// </summary>
    /// <param name="message">The server's explanation.</param>
    public RemoteException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteException" /> class for a prompt the server refused because of a usage window.
    /// </summary>
    /// <param name="message">The server's explanation.</param>
    /// <param name="overLimit">The usage window that made the server refuse the prompt, or <see langword="null" /> when the refusal has another reason.</param>
    public RemoteException(string message, LimitPause? overLimit)
        : base(message) => OverLimit = overLimit;

    /// <summary>
    /// Gets the usage window that is at or past the maximum, which is why the server refused a prompt until the user confirms that the work starts anyway; <see langword="null" /> when the refusal has another reason.
    /// </summary>
    public LimitPause? OverLimit { get; }
}
