namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class OllamaServiceUnitTests
{
    private readonly List<(string Line, string? Input, IReadOnlyDictionary<string, string> Variables)> runs = [];

    private readonly Queue<(int Code, string Output, string Errors)> results = new();

    private readonly Dictionary<string, string?> files = [];

    private bool systemd = true;

    private OllamaServiceUnit Unit(Exception? failure = null) => new((info, input, _) =>
    {
        runs.Add((info.FileName + " " + string.Join(" ", info.ArgumentList), input, info.Environment.Where(pair => pair.Key == "LC_ALL").ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty)));
        return failure is null ? Task.FromResult(results.Count > 0 ? results.Dequeue() : (0, string.Empty, string.Empty)) : Task.FromException<(int, string, string)>(failure);
    }, _ => systemd, path => files.GetValueOrDefault(path))
    {
        DropIn = "/etc/test/ollama.service.d/zz.conf",
        Unit = "test-ollama.service"
    };

    [Fact]
    public async Task IsAvailable_NeedsASystemdMachineWhoseServiceExists()
    {
        results.Enqueue((0, string.Empty, string.Empty));

        Assert.Equal(OperatingSystem.IsLinux(), await Unit().IsAvailable());

        systemd = false;

        Assert.False(await Unit().IsAvailable());
    }

    [Fact]
    public async Task IsAvailable_IsFalseWhenTheServiceIsUnknownOrSystemctlIsMissing()
    {
        results.Enqueue((4, string.Empty, "No files found for test-ollama.service."));

        Assert.False(await Unit().IsAvailable());
        Assert.False(await Unit(new Win32Exception("none")).IsAvailable());
    }

    [Fact]
    public async Task NeedsPassword_IsTrueUnlessSudoWorksWithoutOne()
    {
        results.Enqueue((0, string.Empty, string.Empty));
        results.Enqueue((1, string.Empty, "sudo: a password is required"));

        Assert.False(await Unit().NeedsPassword());
        Assert.True(await Unit().NeedsPassword());
        Assert.True(await Unit(new Win32Exception("none")).NeedsPassword());
        Assert.Equal("sudo -n true", runs[0].Line);
        Assert.Equal("C", runs[0].Variables["LC_ALL"]);
    }

    [Fact]
    public void Matches_ComparesTheDropInWithWhatWouldBeWritten()
    {
        OllamaServiceUnit unit = Unit();

        Assert.True(unit.Matches(false));
        Assert.False(unit.Matches(true));

        files["/etc/test/ollama.service.d/zz.conf"] = unit.Content;

        Assert.True(unit.Matches(true));
        Assert.False(unit.Matches(false));

        files["/etc/test/ollama.service.d/zz.conf"] = "[Service]\n";

        Assert.False(unit.Matches(true));
    }

    [Fact]
    public async Task Apply_WithoutAPasswordRunsOneNonInteractiveSudoThatInstallsTheDropInAndRestartsTheService()
    {
        OllamaServiceUnit unit = Unit();

        RootResult result = await unit.Apply(true, null);

        Assert.Equal(RootOutcome.Done, result.Outcome);
        (string line, string? input, _) = Assert.Single(runs);
        Assert.StartsWith("sudo -n sh -c ", line);
        Assert.Contains("install -D", line);
        Assert.Contains("systemctl restart", line);
        Assert.EndsWith(" /etc/test/ollama.service.d/zz.conf test-ollama.service", line);
        Assert.Null(input);
        Assert.Contains("OLLAMA_KV_CACHE_TYPE=q4_0", unit.Content);
        Assert.Contains("OLLAMA_FLASH_ATTENTION=1", unit.Content);
        Assert.Contains("OLLAMA_LOAD_TIMEOUT=0", unit.Content);
    }

    [Fact]
    public async Task Apply_GivesThePasswordToSudoOnItsStandardInputAndNowhereElse()
    {
        OllamaServiceUnit unit = Unit();

        RootResult result = await unit.Apply(true, "hunter2");

        Assert.Equal(RootOutcome.Done, result.Outcome);
        (string line, string? input, IReadOnlyDictionary<string, string> variables) = Assert.Single(runs);
        Assert.StartsWith("sudo -S -k -p  sh -c ", line);
        Assert.Equal("hunter2\n", input);
        Assert.DoesNotContain("hunter2", line);
        Assert.DoesNotContain(variables.Values, value => value.Contains("hunter2"));
    }

    [Fact]
    public async Task Apply_WhenDisablingRemovesTheDropInAndStagesNothing()
    {
        RootResult result = await Unit().Apply(false, null);

        Assert.Equal(RootOutcome.Done, result.Outcome);
        string line = Assert.Single(runs).Line;
        Assert.Contains("rm -f", line);
        Assert.DoesNotContain("install", line);
        Assert.EndsWith(" sh  /etc/test/ollama.service.d/zz.conf test-ollama.service", line);
    }

    [Fact]
    public async Task Apply_RemovesTheStagedFileAfterwardsWhetherOrNotSudoSucceeded()
    {
        string? staged = null;
        OllamaServiceUnit unit = new((info, _, _) =>
        {
            staged = info.ArgumentList[^3];
            Assert.True(File.Exists(staged));
            Assert.Equal(new OllamaServiceUnit().Content, File.ReadAllText(staged));
            return Task.FromResult((1, string.Empty, "boom"));
        }, _ => true, _ => null);

        RootResult result = await unit.Apply(true, null);

        Assert.Equal(RootOutcome.Failed, result.Outcome);
        Assert.NotNull(staged);
        Assert.False(File.Exists(staged));
    }

    [Theory]
    [InlineData("Sorry, try again.\nsudo: 1 incorrect password attempt")]
    [InlineData("sudo: no password was provided")]
    [InlineData("sudo: a password is required")]
    public async Task Apply_ReportsAPasswordThatSudoRefusedAsWrong(string errors)
    {
        results.Enqueue((1, string.Empty, errors));

        RootResult result = await Unit().Apply(false, "nope");

        Assert.Equal(RootOutcome.WrongPassword, result.Outcome);
    }

    [Theory]
    [InlineData("user is not in the sudoers file.")]
    [InlineData("Sorry, user may not run sudo on box.")]
    [InlineData("Sorry, user is not allowed to execute '/bin/sh' as root")]
    public async Task Apply_ReportsAUserWithoutSudoRightsAsNotAllowed(string errors)
    {
        results.Enqueue((1, string.Empty, errors));

        RootResult result = await Unit().Apply(false, "pw");

        Assert.Equal(RootOutcome.NotAllowed, result.Outcome);
        Assert.Contains("may not use sudo", result.Message);
    }

    [Fact]
    public async Task Apply_ReportsTheFirstLineOfAnyOtherFailure()
    {
        results.Enqueue((5, string.Empty, "\nFailed to restart test-ollama.service: Unit not found.\nsecond line"));
        results.Enqueue((5, string.Empty, string.Empty));

        RootResult first = await Unit().Apply(false, null);
        RootResult second = await Unit().Apply(false, null);

        Assert.Equal((RootOutcome.Failed, "Failed to restart test-ollama.service: Unit not found."), (first.Outcome, first.Message));
        Assert.Equal((RootOutcome.Failed, "Configuring the Ollama service failed."), (second.Outcome, second.Message));
    }

    [Fact]
    public async Task Apply_ReportsAMissingSudoAndASlowRestart()
    {
        RootResult missing = await Unit(new Win32Exception("none")).Apply(false, null);
        OllamaServiceUnit slow = new(async (_, _, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return default;
        }, _ => true, _ => null)
        { Limit = TimeSpan.FromMilliseconds(50) };

        RootResult late = await slow.Apply(false, null);

        Assert.Equal(RootOutcome.NotAllowed, missing.Outcome);
        Assert.Contains("sudo is not installed", missing.Message);
        Assert.Equal((RootOutcome.Failed, "Restarting the Ollama service took too long."), (late.Outcome, late.Message));
    }
}
