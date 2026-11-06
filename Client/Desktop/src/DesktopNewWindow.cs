namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Opens a new window of the desktop client by starting another process of the same program with the same command line, so it uses the same data folder, and leaving it running on its own.
/// </summary>
internal sealed class DesktopNewWindow : INewWindow
{
    /// <inheritdoc />
    public void Open()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this program is not known.");
        ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = false };

        string[] given = Environment.GetCommandLineArgs();

        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase) && given.Length > 0)
        {
            start.ArgumentList.Add(given[0]);
        }

        foreach (string argument in given.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            throw new InvalidOperationException($"A new window could not be opened: {exception.Message}");
        }
    }
}
