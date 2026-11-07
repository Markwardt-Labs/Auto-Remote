namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Reads how much of the subscription of an AI provider is used, from the provider's own report.
/// </summary>
internal interface IUsageService
{
    /// <summary>
    /// Reads the usage limits of a provider as of at most a minute ago, so asking often costs nothing. A reading that is due runs a minimal prompt of the provider, which Live AI does to learn the limits, unless a prompt of a workspace reported them lately.
    /// </summary>
    /// <param name="service">The AI provider.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The limits the provider reports; without any windows when it reports none or could not be read.</returns>
    Task<UsageStatus> Get(AiService service, CancellationToken cancellation);

    /// <summary>
    /// Takes the limits a prompt of a workspace reported, which are as good as a reading and cost nothing.
    /// </summary>
    /// <param name="service">The AI provider.</param>
    /// <param name="windows">The limits.</param>
    void Note(AiService service, IReadOnlyList<PromptWindow> windows);
}

/// <inheritdoc />
internal sealed class UsageService(IPrompterHost host) : IUsageService
{
    private readonly ConcurrentDictionary<AiService, Cached> cache = new();

    private readonly SemaphoreSlim reading = new(1, 1);

    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(1);

    private readonly TimeSpan retry = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public void Note(AiService service, IReadOnlyList<PromptWindow> windows) => cache[service] = new Cached(Status(service, windows), DateTimeOffset.UtcNow + lifetime);

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
            PromptTestResult result = await host.Prompter.Test(host.Setup(service), cancellation);
            return result.IsAvailable ? Status(service, result.Windows ?? []) : null;
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or ArgumentException or RequestException)
        {
            cancellation.ThrowIfCancellationRequested();
            return null;
        }
    }

    private UsageStatus Status(AiService service, IReadOnlyList<PromptWindow> windows) => new() { Service = service, Windows = [.. windows.Select(Period)] };

    private UsagePeriod Period(PromptWindow window)
    {
        bool passed = window.ResetsAt is { } time && time <= DateTimeOffset.UtcNow;
        return new UsagePeriod { Name = window.Name, PercentUsed = passed ? 0 : Math.Round(window.Used * 100, 1), WindowMinutes = (int)window.Length.TotalMinutes, ResetsAt = passed ? null : window.ResetsAt };
    }

    private sealed record Cached(UsageStatus Status, DateTimeOffset Until);
}
