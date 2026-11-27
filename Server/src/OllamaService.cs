namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A running Ollama that Auto Remote started.
/// </summary>
internal interface IOllamaRun : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the process is still running.
    /// </summary>
    bool IsRunning { get; }
}

/// <summary>
/// Keeps the Ollama the local models run on set up the way the environment asks. Ollama reads its settings (the key and value cache type, flash attention, how long a model load may stall) from variables when it starts, so they cannot be changed in a running one: Auto Remote starts Ollama itself with them when it is not running, restarts the one it started when the choice changes, and says so when Ollama runs outside it.
/// </summary>
internal interface IOllamaService : IAsyncDisposable
{
    /// <summary>
    /// Gets what is worth telling the user about the Ollama of the local models, or <see langword="null" /> when there is nothing.
    /// </summary>
    string? Note { get; }

    /// <summary>
    /// Gets a value indicating whether the Ollama of the local models can be configured as a systemd service (Linux with systemd, a model server on this machine and an <c>ollama.service</c> unit), as of the last <see cref="Refresh" />.
    /// </summary>
    bool ServiceControl { get; }

    /// <summary>
    /// Gets a value indicating whether configuring the service needs the sudo password of the server's user, as of the last <see cref="Refresh" />.
    /// </summary>
    bool ServiceNeedsPassword { get; }

    /// <summary>
    /// Looks again at what can be done with the Ollama service on this machine.
    /// </summary>
    /// <param name="environment">The environment whose model server applies.</param>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns>A task that completes when <see cref="ServiceControl" /> and <see cref="ServiceNeedsPassword" /> are up to date.</returns>
    Task Refresh(AiEnvironment environment, CancellationToken cancellation = default);

    /// <summary>
    /// Turns the optimized context of the Ollama service on or off: writes or removes the systemd drop-in with root rights, restarts the service and waits for it to answer. Nothing is done when the service is already as asked. The Ollama that Auto Remote started itself, if any, is stopped first since the service takes its place.
    /// </summary>
    /// <param name="environment">The environment whose model server applies.</param>
    /// <param name="enabled">Whether the optimized context is wanted.</param>
    /// <param name="password">The sudo password of the server's user, or <see langword="null" /> when none is needed. It is never kept.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>How it ended; a failure is the result, never an exception.</returns>
    Task<RootResult> ConfigureService(AiEnvironment environment, bool enabled, string? password, CancellationToken cancellation = default);

    /// <summary>
    /// Makes the Ollama of the environment run the way it asks, as far as Auto Remote can: nothing happens for a model server that is not Ollama on this machine, and a failure is reported as the note, never thrown.
    /// </summary>
    /// <param name="environment">The environment whose model server and choices apply.</param>
    /// <param name="cancellation">Cancels the wait for Ollama to answer.</param>
    /// <returns>A task that completes when Ollama answers, or when it is clear that it will not.</returns>
    Task Ensure(AiEnvironment environment, CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="reachable">Tells whether a model server answers, or <see langword="null" /> to ask its version over HTTP.</param>
/// <param name="launch">Starts Ollama with a command and variables, or <see langword="null" /> to start the process; it gives <see langword="null" /> when it could not be started.</param>
/// <param name="unit">The systemd unit of Ollama, or <see langword="null" /> for the real one.</param>
internal sealed class OllamaService(Func<Uri, CancellationToken, Task<bool>>? reachable = null, Func<string, IReadOnlyDictionary<string, string>, IOllamaRun?>? launch = null, IOllamaServiceUnit? unit = null) : IOllamaService
{
    private static async Task<bool> Answers(Uri server, CancellationToken cancellation)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await client.GetAsync(new Uri(server, "/api/version"), cancellation);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private static IOllamaRun? Launch(string command, IReadOnlyDictionary<string, string> variables)
    {
        ProcessStartInfo info = new(command) { UseShellExecute = false };
        info.ArgumentList.Add("serve");

        foreach ((string name, string value) in variables)
        {
            info.Environment[name] = value;
        }

        try
        {
            return Process.Start(info) is { } process ? new OllamaProcess(process) : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly IOllamaServiceUnit systemd = unit ?? new OllamaServiceUnit();

    private bool control;

    private bool needsPassword;

    private DateTime refreshed = DateTime.MinValue;

    private readonly Func<Uri, CancellationToken, Task<bool>> answers = reachable ?? Answers;

    private readonly Func<string, IReadOnlyDictionary<string, string>, IOllamaRun?> start = launch ?? Launch;

    private readonly SemaphoreSlim working = new(1, 1);

    private IOllamaRun? run;

    private bool optimized;

    private string? note;

    /// <summary>
    /// Gets how long a started Ollama has to answer.
    /// </summary>
    internal TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long a stopped Ollama has to stop answering.
    /// </summary>
    internal TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets how often the wait for Ollama to answer or stop asks.
    /// </summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public string? Note => note;

    /// <inheritdoc />
    public bool ServiceControl => control;

    /// <inheritdoc />
    public bool ServiceNeedsPassword => needsPassword;

    /// <summary>
    /// Gets how long the service has to answer after it was restarted.
    /// </summary>
    internal TimeSpan ServiceWait { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Gets how long a look at what the service allows stays valid.
    /// </summary>
    internal TimeSpan RefreshEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task Refresh(AiEnvironment environment, CancellationToken cancellation = default)
    {
        await working.WaitAsync(cancellation);

        try
        {
            await Look(environment, true, cancellation);
        }
        finally
        {
            working.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RootResult> ConfigureService(AiEnvironment environment, bool enabled, string? password, CancellationToken cancellation = default)
    {
        await working.WaitAsync(cancellation);

        try
        {
            await Look(environment, true, cancellation);

            if (!control || Address(environment) is not { } address)
            {
                return new RootResult(RootOutcome.Failed, "This server cannot configure an Ollama service.");
            }

            if (systemd.Matches(enabled) && await answers(address, cancellation))
            {
                note = enabled ? Configured : null;
                return new RootResult(RootOutcome.Done);
            }

            run?.Dispose();
            run = null;
            RootResult result = await systemd.Apply(enabled, needsPassword ? password : null, cancellation);

            if (result.Outcome != RootOutcome.Done)
            {
                return result;
            }

            DateTime until = DateTime.UtcNow + ServiceWait;

            while (DateTime.UtcNow < until && !await answers(address, cancellation))
            {
                await Task.Delay(Poll, cancellation);
            }

            if (!await answers(address, cancellation))
            {
                return new RootResult(RootOutcome.Failed, "The Ollama service was restarted but did not answer.");
            }

            note = enabled ? Configured : null;
            return result;
        }
        finally
        {
            working.Release();
        }
    }

    /// <inheritdoc />
    public async Task Ensure(AiEnvironment environment, CancellationToken cancellation = default)
    {
        await working.WaitAsync(cancellation);

        try
        {
            await Apply(environment, cancellation);
        }
        finally
        {
            working.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        run?.Dispose();
        run = null;
        return ValueTask.CompletedTask;
    }

    private Uri? Address(AiEnvironment environment) => Uri.TryCreate(environment.LocalServer, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https" && address.IsLoopback ? address : null;

    private async Task Look(AiEnvironment environment, bool force, CancellationToken cancellation)
    {
        if (!force && DateTime.UtcNow - refreshed < RefreshEvery)
        {
            return;
        }

        control = Address(environment) is not null && await systemd.IsAvailable(cancellation);
        needsPassword = control && await systemd.NeedsPassword(cancellation);
        refreshed = DateTime.UtcNow;
    }

    private async Task Apply(AiEnvironment environment, CancellationToken cancellation)
    {
        await Look(environment, false, cancellation);

        if (!Uri.TryCreate(environment.LocalServer, UriKind.Absolute, out Uri? address) || address.Scheme is not ("http" or "https"))
        {
            note = null;
            return;
        }

        if (!address.IsLoopback)
        {
            await Stop(address, cancellation);
            note = environment.LocalOptimizeContext ? "The model server is not on this machine, so Auto Remote cannot set the variables of its Ollama." : null;
            return;
        }

        bool up = await answers(address, cancellation);
        bool wanted = environment.LocalOptimizeContext;

        if (run is { IsRunning: false })
        {
            run = null;
        }

        if (run is not null && (optimized != wanted || !up))
        {
            await Stop(address, cancellation);
            up = await answers(address, cancellation);
        }

        if (run is not null)
        {
            note = Started(wanted);
            return;
        }

        if (up)
        {
            note = control ? (wanted && systemd.Matches(true) ? Configured : wanted ? $"Ollama runs as a service that Auto Remote has not configured: turn Optimize context off and on again to let it (it asks for your password), or set {Variables(true)} in the service yourself." : "Ollama runs as a service outside Auto Remote: turn Optimize context on to let Auto Remote configure it (it asks for your password when it needs one).") : $"Ollama already runs outside Auto Remote, so Auto Remote cannot set its variables: start it with {Variables(wanted)} (stop it first and Auto Remote starts it that way when a local model is used).";
            return;
        }

        await Begin(environment, address, wanted, cancellation);
    }

    private async Task Begin(AiEnvironment environment, Uri address, bool wanted, CancellationToken cancellation)
    {
        Dictionary<string, string> variables = new() { ["OLLAMA_HOST"] = $"{address.Host}:{address.Port}", ["OLLAMA_LOAD_TIMEOUT"] = "0" };

        if (wanted)
        {
            variables["OLLAMA_KV_CACHE_TYPE"] = "q4_0";
            variables["OLLAMA_FLASH_ATTENTION"] = "1";
        }

        IOllamaRun? started = start(string.IsNullOrWhiteSpace(environment.OllamaCommand) ? "ollama" : environment.OllamaCommand, variables);

        if (started is null)
        {
            note = $"Ollama could not be started with the command '{environment.OllamaCommand}'.";
            return;
        }

        DateTime until = DateTime.UtcNow + StartWait;

        while (started.IsRunning && DateTime.UtcNow < until && !await answers(address, cancellation))
        {
            await Task.Delay(Poll, cancellation);
        }

        if (!started.IsRunning || !await answers(address, cancellation))
        {
            started.Dispose();
            note = "Ollama was started but did not answer.";
            return;
        }

        run = started;
        optimized = wanted;
        note = Started(wanted);
    }

    private async Task Stop(Uri address, CancellationToken cancellation)
    {
        if (run is null)
        {
            return;
        }

        run.Dispose();
        run = null;
        DateTime until = DateTime.UtcNow + StopWait;

        while (DateTime.UtcNow < until && await answers(address, cancellation))
        {
            await Task.Delay(Poll, cancellation);
        }
    }

    private string Configured => "Auto Remote configured the Ollama service with a quantized (q4_0) key and value cache, flash attention and no limit on how long a model load may stall.";

    private string Started(bool wanted) => wanted ? "Auto Remote started Ollama with a quantized (q4_0) key and value cache, flash attention and no limit on how long a model load may stall." : "Auto Remote started Ollama with no limit on how long a model load may stall.";

    private string Variables(bool wanted) => wanted ? "OLLAMA_KV_CACHE_TYPE=q4_0, OLLAMA_FLASH_ATTENTION=1 and OLLAMA_LOAD_TIMEOUT=0" : "OLLAMA_LOAD_TIMEOUT=0";

    private sealed class OllamaProcess(Process process) : IOllamaRun
    {
        public bool IsRunning
        {
            get
            {
                try
                {
                    return !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }

            process.Dispose();
        }
    }
}
