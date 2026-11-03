namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The usage limits of the AI service of the focused workspace, kept up to date while one is focused.
/// </summary>
internal sealed class UsageViewModel(IServerConnection connection) : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource stopping = new();

    private readonly TimeSpan interval = TimeSpan.FromMinutes(1);

    private AiService? service;

    private UsageStatus? status;

    private bool polling;

    /// <summary>
    /// Gets the name of the AI service whose limits are shown.
    /// </summary>
    public string ServiceName => (service ?? AiService.ClaudeCode).Label();

    /// <summary>
    /// Gets a value indicating whether the service reported any limit.
    /// </summary>
    public bool HasLimits => Windows.Count > 0;

    /// <summary>
    /// Gets every window the service reports, in the order it reports them.
    /// </summary>
    public IReadOnlyList<UsageLine> Windows => [.. (status?.Windows ?? []).Select(Line)];

    /// <summary>
    /// Gets the pace that colors the usage icon: the pace of the shortest window.
    /// </summary>
    public string IconPace => (status?.Windows.MinBy(window => window.WindowMinutes)?.Pace(DateTimeOffset.UtcNow) ?? UsagePace.Even).ToString();

    /// <summary>
    /// Shows the limits of an AI service and keeps them up to date, or stops showing any.
    /// </summary>
    /// <param name="service">The AI service, or <see langword="null" /> when no workspace is focused.</param>
    /// <returns>A task that completes once the first read has finished.</returns>
    public async Task Show(AiService? service)
    {
        if (this.service != service)
        {
            this.service = service;
            status = null;
            RaiseAll();
        }

        if (service is null)
        {
            return;
        }

        if (!polling)
        {
            polling = true;
            _ = Poll();
        }

        await Refresh();
    }

    /// <summary>
    /// Stops keeping the limits up to date.
    /// </summary>
    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    private async Task Poll()
    {
        using PeriodicTimer timer = new(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                if (!await Refresh())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> Refresh()
    {
        if (service is not { } asked)
        {
            return true;
        }

        try
        {
            UsageStatus? read = await connection.GetUsage(asked, stopping.Token);

            if (service == asked)
            {
                status = read;
                RaiseAll();
            }

            return true;
        }
        catch (RemoteException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private UsageLine Line(UsagePeriod period) => new() { Name = period.Name, Text = $"{Math.Round(period.PercentUsed):0}% used", Reset = Reset(period), Pace = period.Pace(DateTimeOffset.UtcNow).ToString() };

    private string Reset(UsagePeriod period)
    {
        if (period.ResetsAt is not { } resets)
        {
            return string.Empty;
        }

        TimeSpan left = resets - DateTimeOffset.UtcNow;

        if (left <= TimeSpan.Zero)
        {
            return "Resets now";
        }

        return left.TotalDays >= 1 ? $"Resets in {(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"Resets in {(int)left.TotalHours}h {left.Minutes}m" : $"Resets in {Math.Max(1, left.Minutes)}m";
    }

    private void RaiseAll()
    {
        Raise(nameof(ServiceName));
        Raise(nameof(HasLimits));
        Raise(nameof(Windows));
        Raise(nameof(IconPace));
    }
}
