namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Plays the ding on a desktop: the Windows multimedia API on Windows, otherwise the first command line player the system has.
/// </summary>
internal sealed class DesktopChime : IChime
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DesktopChime" /> class.
    /// </summary>
    /// <param name="sound">The ding to play.</param>
    public DesktopChime(ChimeSound sound)
    {
        wav = sound.Wav.ToArray();
        pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
    }

    private readonly byte[] wav;

    private readonly GCHandle pinned;

    private readonly string file = Path.Combine(Path.GetTempPath(), "auto-remote-ding.wav");

    private readonly List<(string command, string[] arguments)> players = [("paplay", []), ("pw-play", []), ("aplay", ["-q"]), ("afplay", [])];

    /// <inheritdoc />
    public void Play() => _ = Task.Run(Run);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(nint sound, nint module, uint flags);

    private async Task Run()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                PlaySound(pinned.AddrOfPinnedObject(), 0, 0x0004 | 0x0001 | 0x0002);
                return;
            }

            await File.WriteAllBytesAsync(file, wav);

            foreach ((string command, string[] arguments) in players)
            {
                if (await Try(command, arguments))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private async Task<bool> Try(string command, string[] arguments)
    {
        ProcessStartInfo start = new(command, [.. arguments, file]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
