namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Reads how much of the subscription of an AI service is used, from the service's own report.
/// </summary>
internal interface IUsageService
{
    /// <summary>
    /// Supplies the settings that name the AI executables.
    /// </summary>
    /// <param name="config">The server settings.</param>
    void Configure(ServerConfig config);

    /// <summary>
    /// Reads the usage limits of a service as of at most a minute ago, so asking often costs nothing.
    /// </summary>
    /// <param name="service">The AI service.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The limits the service reports; without any windows when it reports none or could not be read.</returns>
    Task<UsageStatus> Get(AiService service, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class UsageService(Func<ServerConfig, AiService, IPromptProvider> createProvider) : IUsageService
{
    private readonly ConcurrentDictionary<AiService, Cached> cache = new();

    private readonly SemaphoreSlim reading = new(1, 1);

    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(1);

    private readonly TimeSpan retry = TimeSpan.FromSeconds(10);

    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    /// <inheritdoc />
    public void Configure(ServerConfig config) => this.config = config;

    /// <inheritdoc />
    public async Task<UsageStatus> Get(AiService service, CancellationToken cancellation)
    {
        await reading.WaitAsync(cancellation);

        try
        {
            if (cache.TryGetValue(service, out Cached? known) && DateTimeOffset.UtcNow < known.Until)
            {
                return known.Status;
            }

            UsageStatus? read = await Read(service, cancellation);
            Cached next = read is null ? new Cached(known?.Status ?? new UsageStatus { Service = service }, DateTimeOffset.UtcNow + retry) : new Cached(read, DateTimeOffset.UtcNow + lifetime);
            cache[service] = next;
            return next.Status;
        }
        finally
        {
            reading.Release();
        }
    }

    private async Task<UsageStatus?> Read(AiService service, CancellationToken cancellation)
    {
        try
        {
            IReadOnlyList<PromptWindow> windows = await createProvider(config, service).GetWindows(cancellation);
            return new UsageStatus { Service = service, Windows = [.. windows.Select(Period)] };
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }

    private UsagePeriod Period(PromptWindow window)
    {
        bool passed = window.ResetsAt is { } time && time <= DateTimeOffset.UtcNow;
        return new UsagePeriod { Name = window.Name, PercentUsed = passed ? 0 : Math.Round(window.Used * 100, 1), WindowMinutes = (int)window.Length.TotalMinutes, ResetsAt = passed ? null : window.ResetsAt };
    }

    private sealed record Cached(UsageStatus Status, DateTimeOffset Until);
}
