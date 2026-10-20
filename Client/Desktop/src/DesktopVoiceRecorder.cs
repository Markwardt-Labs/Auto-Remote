namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Records speech on a desktop: the Windows multimedia API on Windows, otherwise the first command line recorder the system has (<c>parecord</c> of PulseAudio and PipeWire, or <c>arecord</c> of ALSA).
/// </summary>
internal sealed class DesktopVoiceRecorder : IVoiceRecorder
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int Mci(string command, StringBuilder? result, int length, nint callback);

    [DllImport("winmm.dll", EntryPoint = "mciGetErrorStringW", CharSet = CharSet.Unicode)]
    private static extern bool MciError(int error, StringBuilder text, int length);

    private readonly List<(string command, string[] arguments)> recorders = [("parecord", ["--raw", "--format=s16le", "--rate=16000", "--channels=1"]), ("arecord", ["-q", "-t", "raw", "-f", "S16_LE", "-r", "16000", "-c", "1"])];

    private Process? process;

    private Task<byte[]>? reading;

    private bool recording;

    /// <inheritdoc />
    public int SampleRate => 16000;

    /// <inheritdoc />
    public async Task Start(CancellationToken cancellation)
    {
        if (recording)
        {
            throw new InvalidOperationException("Voice input is already recording.");
        }

        if (OperatingSystem.IsWindows())
        {
            StartWindows();
        }
        else
        {
            await StartCommand(cancellation);
        }

        recording = true;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> Stop()
    {
        if (!recording)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        recording = false;
        return OperatingSystem.IsWindows() ? StopWindows() : await StopCommand();
    }

    private async Task StartCommand(CancellationToken cancellation)
    {
        foreach ((string command, string[] arguments) in recorders)
        {
            ProcessStartInfo start = new(command, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            Process? started;

            try
            {
                started = Process.Start(start);
            }
            catch (Win32Exception)
            {
                continue;
            }

            if (started is null)
            {
                continue;
            }

            _ = started.StandardError.ReadToEndAsync(cancellation);
            Task<byte[]> audio = Read(started.StandardOutput.BaseStream);
            await Task.Delay(300, cancellation);

            if (started.HasExited)
            {
                started.Dispose();
                continue;
            }

            process = started;
            reading = audio;
            return;
        }

        throw new InvalidOperationException("No microphone could be used. Voice input needs parecord (pulseaudio-utils) or arecord (alsa-utils) and a microphone.");
    }

    private async Task<byte[]> Read(Stream source)
    {
        using MemoryStream audio = new();
        await source.CopyToAsync(audio);
        return audio.ToArray();
    }

    private async Task<ReadOnlyMemory<byte>> StopCommand()
    {
        Process ended = process!;
        process = null;
        Task<byte[]> audio = reading!;
        reading = null;

        try
        {
            ended.Kill();
            await ended.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            ended.Dispose();
        }

        byte[] bytes = await audio;
        return bytes.AsMemory(0, bytes.Length & ~1);
    }

    private void StartWindows()
    {
        Mci("close auto-remote-voice", null, 0, 0);
        Check(Mci("open new type waveaudio alias auto-remote-voice", null, 0, 0));
        Check(Mci("set auto-remote-voice bitspersample 16 channels 1 samplespersec 16000 alignment 2 bytespersec 32000", null, 0, 0));
        Check(Mci("record auto-remote-voice", null, 0, 0));
    }

    private ReadOnlyMemory<byte> StopWindows()
    {
        string file = Path.Combine(Path.GetTempPath(), "auto-remote-voice-" + Guid.NewGuid().ToString("N") + ".wav");

        try
        {
            Mci("stop auto-remote-voice", null, 0, 0);
            Check(Mci($"save auto-remote-voice \"{file}\"", null, 0, 0));
            byte[] wav = File.ReadAllBytes(file);
            int at = 12;

            while (at + 8 <= wav.Length)
            {
                int size = BitConverter.ToInt32(wav, at + 4);

                if (Encoding.ASCII.GetString(wav, at, 4) == "data")
                {
                    return wav.AsMemory(at + 8, Math.Min(size, wav.Length - at - 8) & ~1);
                }

                at += 8 + size + (size & 1);
            }

            return ReadOnlyMemory<byte>.Empty;
        }
        finally
        {
            Mci("close auto-remote-voice", null, 0, 0);
            File.Delete(file);
        }
    }

    private void Check(int error)
    {
        if (error != 0)
        {
            StringBuilder text = new(256);
            MciError(error, text, text.Capacity);
            Mci("close auto-remote-voice", null, 0, 0);
            throw new InvalidOperationException($"No microphone could be used: {text}");
        }
    }
}
