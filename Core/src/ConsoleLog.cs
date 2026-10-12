namespace Markwardt.AutoRemote;

/// <summary>
/// Writes diagnostic lines about connections, shared by the server and the clients.
/// </summary>
public interface ILog
{
    /// <summary>
    /// Writes one line.
    /// </summary>
    /// <param name="message">The message, without a timestamp.</param>
    void Write(string message);
}

/// <summary>
/// Writes timestamped lines to the console, which is the terminal on a desktop or server and the device log (logcat) on Android.
/// </summary>
public sealed class ConsoleLog : ILog
{
    /// <inheritdoc />
    public void Write(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}
