namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Turns speech into text inside the server with Whisper (whisper.cpp), on the GPU through Vulkan when the machine has one and on the CPU otherwise.
/// </summary>
internal interface IWhisperEngine
{
    /// <summary>
    /// Takes up the server settings.
    /// </summary>
    /// <param name="config">The server settings.</param>
    void Configure(ServerConfig config);

    /// <summary>
    /// Makes sure the model is on disk, downloading it the first time, so the first recording does not have to wait for it.
    /// </summary>
    /// <param name="cancellation">Cancels the download.</param>
    /// <returns>A task that completes once the model is ready.</returns>
    /// <exception cref="RequestException">Voice input is turned off, the model name is not valid or the model could not be downloaded.</exception>
    Task Prepare(CancellationToken cancellation);

    /// <summary>
    /// Transcribes recorded speech, loading the model if it is not loaded.
    /// </summary>
    /// <param name="wav">The WAV audio, 16 kHz, mono, 16 bit.</param>
    /// <param name="cancellation">Cancels the transcription.</param>
    /// <returns>The text, empty when nothing was heard.</returns>
    /// <exception cref="RequestException">Voice input is turned off, the model is missing or cannot be loaded, or the audio is not valid.</exception>
    Task<string> Transcribe(ReadOnlyMemory<byte> wav, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class WhisperEngine : IWhisperEngine
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly TimeSpan idle = TimeSpan.FromMinutes(10);

    private readonly string baseDirectory = "APP_CONTEXT_BASE_DIRECTORY";

    private readonly string nativePrefix = "whisper-native/";

    private readonly string source = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    private readonly Regex name = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly Regex annotation = new(@"^[\[(].*[\])]$", RegexOptions.Compiled);

    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    private WhisperFactory? factory;

    private CancellationTokenSource? unloading;

    /// <inheritdoc />
    public void Configure(ServerConfig config) => this.config = config;

    /// <inheritdoc />
    public async Task Prepare(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            await Model(cancellation);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> Transcribe(ReadOnlyMemory<byte> wav, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            unloading?.Cancel();
            string path = await Model(cancellation);
            factory ??= await Load(path, cancellation);
            StringBuilder text = new();

            await using (WhisperProcessor processor = Processor(path))
            {
                try
                {
                    using MemoryStream stream = new(wav.ToArray());

                    await foreach (SegmentData segment in processor.ProcessAsync(stream, cancellation))
                    {
                        string part = segment.Text.Trim();

                        if (part.Length > 0 && !annotation.IsMatch(part))
                        {
                            text.Append(text.Length > 0 ? " " : string.Empty).Append(part);
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
                {
                    throw new RequestException($"The recording could not be read: {exception.Message}");
                }
            }

            Unload();
            return text.ToString();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WhisperFactory> Load(string path, CancellationToken cancellation)
    {
        string natives = await ExtractNatives(cancellation);
        object? original = AppContext.GetData(baseDirectory);

        try
        {
            // Whisper.net only looks for its libraries below the app's base directory, which is not writable or even a real folder for a single file app.
            AppContext.SetData(baseDirectory, natives);
            return WhisperFactory.FromPath(path);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new RequestException($"The speech model {Path.GetFileName(path)} could not be loaded: {exception.Message}");
        }
        finally
        {
            AppContext.SetData(baseDirectory, original);
        }
    }

    private async Task<string> ExtractNatives(CancellationToken cancellation)
    {
        string root = Path.Combine(config.DataFolder, "native", typeof(WhisperFactory).Assembly.GetName().Version!.ToString());
        string folder = Path.Combine(root, "runtimes", "vulkan", OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
        Directory.CreateDirectory(folder);
        Assembly assembly = typeof(WhisperEngine).Assembly;

        foreach (string resource in assembly.GetManifestResourceNames().Where(item => item.StartsWith(nativePrefix, StringComparison.Ordinal)))
        {
            string file = Path.Combine(folder, resource[nativePrefix.Length..]);
            await using Stream embedded = assembly.GetManifestResourceStream(resource)!;

            if (File.Exists(file) && new FileInfo(file).Length == embedded.Length)
            {
                continue;
            }

            string partial = file + ".partial";

            await using (FileStream target = File.Create(partial))
            {
                await embedded.CopyToAsync(target, cancellation);
            }

            File.Move(partial, file, true);
        }

        return root + Path.DirectorySeparatorChar;
    }

    private WhisperProcessor Processor(string path)
    {
        try
        {
            return factory!.CreateBuilder().WithLanguage(path.Contains(".en", StringComparison.OrdinalIgnoreCase) ? "en" : "auto").Build();
        }
        catch (WhisperModelLoadException exception)
        {
            factory!.Dispose();
            factory = null;
            throw new RequestException($"The speech model {Path.GetFileName(path)} could not be loaded: {exception.Message}");
        }
    }

    private void Unload()
    {
        CancellationTokenSource source = new();
        unloading = source;
        _ = Release(source.Token);
    }

    private async Task Release(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(idle, cancellation);
            await gate.WaitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            factory?.Dispose();
            factory = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> Model(CancellationToken cancellation)
    {
        string model = config.SpeechModel?.Trim() ?? string.Empty;

        if (model.Length == 0)
        {
            throw new RequestException("Voice input is turned off on the server: set speechModel or speechCommand in its config.");
        }

        if (File.Exists(model))
        {
            return model;
        }

        if (!name.IsMatch(model))
        {
            throw new RequestException($"The speech model {model} is neither a model file nor a Whisper model name such as small.en.");
        }

        string file = Path.Combine(config.DataFolder, "models", $"ggml-{model}.bin");

        if (!File.Exists(file))
        {
            await Download($"{source}/ggml-{model}.bin", file, cancellation);
        }

        return file;
    }

    private async Task Download(string url, string file, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string partial = file + ".download";

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();

            await using (FileStream target = File.Create(partial))
            {
                await response.Content.CopyToAsync(target, cancellation);
            }

            File.Move(partial, file, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            File.Delete(partial);
            throw new RequestException($"The speech model could not be downloaded from {url}: {exception.Message}");
        }
    }
}
