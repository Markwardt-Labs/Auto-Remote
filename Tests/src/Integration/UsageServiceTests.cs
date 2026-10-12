namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class UsageServiceTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose() => temp.Dispose();

    private ServerConfig Claude(string reply, int exitCode = 0)
    {
        string result = JsonSerializer.Serialize(new { type = "result", subtype = "success", result = reply });
        File.WriteAllText(temp.Combine("out.txt"), "{\"type\":\"system\"}\n" + result + "\n");
        string fake = temp.Combine("fake-claude.sh");
        File.WriteAllText(fake, $"#!/bin/sh\ncat > \"{temp.Combine("in.txt")}\"\ncat \"{temp.Combine("out.txt")}\"\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new ServerConfig { Port = 0, WebPort = 0, Key = "k", ClaudeCommand = fake };
    }

    private void Rollout(string name, params string[] lines)
    {
        Directory.CreateDirectory(temp.Combine("codex/sessions/2026/10/11"));
        File.WriteAllLines(temp.Combine($"codex/sessions/2026/10/11/{name}"), lines);
    }

    private string TokenCount(double primary, long primaryResets, double secondary, long secondaryResets) => JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "token_count", rate_limits = new { primary = new { used_percent = primary, window_minutes = 300, resets_at = primaryResets }, secondary = new { used_percent = secondary, window_minutes = 10080, resets_at = secondaryResets } } } });

    [Fact]
    public async Task Get_ReadsTheClaudeSessionAndWeekFromTheUsageReport()
    {
        string report = "You are currently using your subscription\n\nCurrent session: 7% used · resets Oct 11, 7:20pm (America/Chicago)\nCurrent week (all models): 25% used · resets Oct 17, 2am (America/Chicago)\nCurrent week (Fable): 0% used · resets Oct 17, 2am (America/Chicago)\n";
        UsageService service = new();
        service.Configure(Claude(report));

        UsageStatus status = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Equal(AiService.ClaudeCode, status.Service);
        Assert.Equal((7, 300), (status.Session!.PercentUsed, status.Session.WindowMinutes));
        Assert.Equal((25, 10080), (status.Week!.PercentUsed, status.Week.WindowMinutes));
        Assert.NotNull(status.Session.ResetsAt);
        Assert.Contains("/usage", File.ReadAllText(temp.Combine("in.txt")));
    }

    [Fact]
    public async Task Get_ReusesAResultForAMinute()
    {
        UsageService service = new();
        service.Configure(Claude("Current session: 7% used · resets Oct 11, 7:20pm (America/Chicago)\n"));
        await service.Get(AiService.ClaudeCode, CancellationToken.None);
        File.Delete(temp.Combine("in.txt"));

        UsageStatus again = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Equal(7, again.Session!.PercentUsed);
        Assert.False(File.Exists(temp.Combine("in.txt")));
    }

    [Fact]
    public async Task Get_ReportsNoWindowsWhenClaudeCannotBeRead()
    {
        UsageService service = new();
        service.Configure(new ServerConfig { Port = 0, WebPort = 0, Key = "k", ClaudeCommand = "/definitely/missing" });

        UsageStatus status = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Equal(new UsageStatus { Service = AiService.ClaudeCode }, status);
    }

    [Fact]
    public async Task Get_ReadsTheCodexLimitsFromTheNewestSessionThatHasThem()
    {
        long soon = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        long later = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();
        Rollout("rollout-2026-10-11T10-00-00-aaaa.jsonl", TokenCount(1, soon, 2, later));
        Rollout("rollout-2026-10-11T11-00-00-bbbb.jsonl", "{\"type\":\"other\"}", TokenCount(10, soon, 20, later), TokenCount(30.04, soon, 40, later));
        Rollout("rollout-2026-10-11T12-00-00-cccc.jsonl", "{\"type\":\"session_meta\"}");

        UsageStatus status = await new UsageService(temp.Combine("codex")).Get(AiService.Codex, CancellationToken.None);

        Assert.Equal((30, 300, soon), (status.Session!.PercentUsed, status.Session.WindowMinutes, status.Session.ResetsAt!.Value.ToUnixTimeSeconds()));
        Assert.Equal((40, 10080), (status.Week!.PercentUsed, status.Week.WindowMinutes));
    }

    [Fact]
    public async Task Get_TreatsACodexWindowThatAlreadyResetAsUnused()
    {
        Rollout("rollout-2026-10-11T10-00-00-aaaa.jsonl", TokenCount(90, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), 50, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()));

        UsageStatus status = await new UsageService(temp.Combine("codex")).Get(AiService.Codex, CancellationToken.None);

        Assert.Equal(0, status.Session!.PercentUsed);
        Assert.Null(status.Session.ResetsAt);
        Assert.Equal(50, status.Week!.PercentUsed);
    }

    [Fact]
    public async Task Get_ReportsNoWindowsWhenCodexHasNoSessions() => Assert.Equal(new UsageStatus { Service = AiService.Codex }, await new UsageService(temp.Combine("none")).Get(AiService.Codex, CancellationToken.None));
}
