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
    public bool HasLimits => status is { Session: not null } or { Week: not null };

    /// <summary>
    /// Gets the session usage as text, or an empty text when the service reports none.
    /// </summary>
    public string SessionText => Describe(status?.Session);

    /// <summary>
    /// Gets when the session resets as text.
    /// </summary>
    public string SessionReset => Reset(status?.Session);

    /// <summary>
    /// Gets the pace of the session usage, which colors the icon: <c>VeryLow</c> green, <c>Low</c> blue, <c>Even</c> normal, <c>High</c> orange and <c>VeryHigh</c> red.
    /// </summary>
    public string SessionPace => (status?.Session?.Pace(DateTimeOffset.UtcNow) ?? UsagePace.Even).ToString();

    /// <summary>
    /// Gets the weekly usage as text, or an empty text when the service reports none.
    /// </summary>
    public string WeekText => Describe(status?.Week);

    /// <summary>
    /// Gets when the week resets as text.
    /// </summary>
    public string WeekReset => Reset(status?.Week);

    /// <summary>
    /// Gets the pace of the weekly usage.
    /// </summary>
    public string WeekPace => (status?.Week?.Pace(DateTimeOffset.UtcNow) ?? UsagePace.Even).ToString();

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

    private string Describe(UsagePeriod? period) => period is null ? string.Empty : $"{Math.Round(period.PercentUsed):0}% used";

    private string Reset(UsagePeriod? period)
    {
        if (period?.ResetsAt is not { } resets)
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
        Raise(nameof(SessionText));
        Raise(nameof(SessionReset));
        Raise(nameof(SessionPace));
        Raise(nameof(WeekText));
        Raise(nameof(WeekReset));
        Raise(nameof(WeekPace));
    }
}
