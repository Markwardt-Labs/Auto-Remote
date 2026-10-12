namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Reads the usage limits of the AI services.
/// </summary>
internal interface IUsageService
{
    /// <summary>
    /// Supplies the settings used to run the AI services.
    /// </summary>
    /// <param name="config">The server settings.</param>
    void Configure(ServerConfig config);

    /// <summary>
    /// Reads the usage limits of a service. Reading costs a process start, so a result is reused for a minute and a failed read keeps showing the last good result.
    /// </summary>
    /// <param name="service">The AI service.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The limits; with no windows when the service reports none or could not be read.</returns>
    Task<UsageStatus> Get(AiService service, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed partial class UsageService(string? codexHome = null) : IUsageService
{
    private readonly ConcurrentDictionary<AiService, Cached> cache = new();

    private readonly SemaphoreSlim reading = new(1, 1);

    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(1);

    private readonly TimeSpan retry = TimeSpan.FromSeconds(10);

    private readonly TimeSpan queryTimeout = TimeSpan.FromSeconds(60);

    private readonly string[] resetFormats = ["MMM d, h:mmtt yyyy", "MMM d, htt yyyy"];

    private readonly string codexFolder = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

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

            UsageStatus? read = service == AiService.Codex ? await ReadCodex(cancellation) : await ReadClaude(cancellation);
            Cached next = read is null ? new Cached(known?.Status ?? new UsageStatus { Service = service }, DateTimeOffset.UtcNow + retry) : new Cached(read, DateTimeOffset.UtcNow + lifetime);
            cache[service] = next;
            return next.Status;
        }
        finally
        {
            reading.Release();
        }
    }

    [GeneratedRegex(@"^(?<pct>\d+)% used · resets (?<resets>.+?)(?:\s*\((?<zone>[^)]+)\))?\s*$")]
    private static partial Regex ClaudeLine();

    private async Task<UsageStatus?> ReadClaude(CancellationToken cancellation)
    {
        ProcessStartInfo start = new(config.ClaudeCommand)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in (string[])["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--model", "sonnet"])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(queryTimeout);

        try
        {
            process.Start();
            Task<string> errors = process.StandardError.ReadToEndAsync(limit.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = "/usage" } }).AsMemory(), limit.Token);
            process.StandardInput.Close();
            string output = await process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            await errors;
            return ParseClaude(output);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();

            try
            {
                process.Kill(true);
            }
            catch (Exception killing) when (killing is InvalidOperationException or Win32Exception)
            {
            }

            return null;
        }
    }

    private UsageStatus? ParseClaude(string output)
    {
        string? report = null;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out JsonElement type) && type.GetString() == "result" && root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.String)
                {
                    report = result.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (report is null)
        {
            return null;
        }

        return new UsageStatus { Service = AiService.ClaudeCode, Session = ClaudePeriod(report, "Current session: ", 300), Week = ClaudePeriod(report, "Current week (all models): ", 10080) };
    }

    private UsagePeriod? ClaudePeriod(string report, string prefix, int windowMinutes)
    {
        foreach (string line in report.Split('\n'))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            Match match = ClaudeLine().Match(line[prefix.Length..].Trim());
            return match.Success ? new UsagePeriod { PercentUsed = int.Parse(match.Groups["pct"].Value), WindowMinutes = windowMinutes, ResetsAt = ResetTime(match.Groups["resets"].Value.Trim(), match.Groups["zone"] is { Success: true } zone ? zone.Value : null) } : null;
        }

        return null;
    }

    private DateTimeOffset? ResetTime(string resets, string? zoneName)
    {
        if (zoneName is null)
        {
            return null;
        }

        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
            DateTime now = DateTime.UtcNow;
            DateTime? utc = InYear(resets, now.Year, zone);

            if (utc < now.AddDays(-1))
            {
                utc = InYear(resets, now.Year + 1, zone) ?? utc;
            }

            return utc is { } resolved ? new DateTimeOffset(resolved, TimeSpan.Zero) : null;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private DateTime? InYear(string resets, int year, TimeZoneInfo zone) => DateTime.TryParseExact($"{resets} {year}", resetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed) ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone) : null;

    private async Task<UsageStatus?> ReadCodex(CancellationToken cancellation)
    {
        string sessions = Path.Combine(codexFolder, "sessions");

        if (!Directory.Exists(sessions))
        {
            return null;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.Ordinal).Take(5))
            {
                using JsonDocument? limits = await LastRateLimits(file, cancellation);

                if (limits is not null)
                {
                    return new UsageStatus { Service = AiService.Codex, Session = CodexPeriod(limits.RootElement, "primary"), Week = CodexPeriod(limits.RootElement, "secondary") };
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private async Task<JsonDocument?> LastRateLimits(string file, CancellationToken cancellation)
    {
        string? found = null;

        using StreamReader reader = new(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (line.Contains("\"rate_limits\"", StringComparison.Ordinal) && line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                found = line;
            }
        }

        if (found is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(found);
            return document.RootElement.GetProperty("payload").TryGetProperty("rate_limits", out JsonElement limits) && limits.ValueKind == JsonValueKind.Object ? JsonDocument.Parse(limits.GetRawText()) : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private UsagePeriod? CodexPeriod(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out JsonElement period) || period.ValueKind != JsonValueKind.Object || !period.TryGetProperty("used_percent", out JsonElement used) || !period.TryGetProperty("window_minutes", out JsonElement window))
        {
            return null;
        }

        DateTimeOffset? resets = period.TryGetProperty("resets_at", out JsonElement at) && at.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()) : null;
        bool passed = resets is { } time && time <= DateTimeOffset.UtcNow;
        return new UsagePeriod { PercentUsed = passed ? 0 : Math.Round(used.GetDouble(), 1), WindowMinutes = window.GetInt32(), ResetsAt = passed ? null : resets };
    }

    private sealed record Cached(UsageStatus Status, DateTimeOffset Until);
}
