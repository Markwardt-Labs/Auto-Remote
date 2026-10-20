namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Turns recorded speech into text with the speech command from the server config, for the voice input of the clients.
/// </summary>
internal interface ISpeechService
{
    /// <summary>
    /// Takes up the server settings.
    /// </summary>
    /// <param name="config">The server settings.</param>
    void Configure(ServerConfig config);

    /// <summary>
    /// Transcribes recorded speech.
    /// </summary>
    /// <param name="data">The base64 WAV audio.</param>
    /// <param name="cancellation">Cancels the transcription, which ends the speech command.</param>
    /// <returns>The text the speech command printed, trimmed; empty when nothing was heard.</returns>
    /// <exception cref="RequestException">The server has no speech command, the audio is not base64 or it is empty or larger than the limit, or the command failed or ran too long.</exception>
    Task<string> Transcribe(string data, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SpeechService(IWhisperEngine engine) : ISpeechService
{
    private readonly int maxAudio = 4 * 1024 * 1024;

    private readonly TimeSpan timeout = TimeSpan.FromMinutes(2);

    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    /// <inheritdoc />
    public void Configure(ServerConfig config)
    {
        this.config = config;
        engine.Configure(config);

        if (string.IsNullOrWhiteSpace(config.SpeechCommand) && !string.IsNullOrWhiteSpace(config.SpeechModel))
        {
            _ = Prefetch();
        }
    }

    /// <inheritdoc />
    public async Task<string> Transcribe(string data, CancellationToken cancellation)
    {
        byte[] bytes = new byte[data.Length / 4 * 3 + 3];

        if (!Convert.TryFromBase64String(data, bytes, out int length) || length == 0)
        {
            throw new RequestException("The recording is empty or not valid base64.");
        }

        if (length > maxAudio)
        {
            throw new RequestException($"The recording is larger than {maxAudio / 1024 / 1024} MB.");
        }

        if (string.IsNullOrWhiteSpace(config.SpeechCommand))
        {
            return (await engine.Transcribe(bytes.AsMemory(0, length), cancellation)).Trim();
        }

        string folder = Path.Combine(Path.GetTempPath(), "auto-remote-speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string file = Path.Combine(folder, "speech.wav");
            await File.WriteAllBytesAsync(file, bytes.AsMemory(0, length), cancellation);
            return await Run(file, cancellation);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private async Task Prefetch()
    {
        try
        {
            await engine.Prepare(CancellationToken.None);
        }
        catch (RequestException)
        {
        }
    }

    private async Task<string> Run(string file, CancellationToken cancellation)
    {
        ProcessStartInfo start = new(config.SpeechCommand!) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };

        foreach (string argument in config.SpeechArguments)
        {
            start.ArgumentList.Add(argument.Replace("{file}", file, StringComparison.Ordinal));
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        Process process;

        try
        {
            process = Process.Start(start) ?? throw new RequestException("The speech command could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new RequestException($"The speech command could not be started: {exception.Message}");
        }

        using (process)
        {
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(limit.Token);

            try
            {
                await process.WaitForExitAsync(limit.Token);
                string text = await output;
                string error = await errors;

                if (process.ExitCode != 0)
                {
                    string reason = error.Trim().Split('\n').LastOrDefault(line => line.Length > 0) ?? $"exit code {process.ExitCode}";
                    throw new RequestException($"The speech command failed: {reason}");
                }

                return text.Trim();
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                cancellation.ThrowIfCancellationRequested();
                throw new RequestException("The speech command took too long.");
            }
        }
    }
}
