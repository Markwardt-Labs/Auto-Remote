namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class BuiltInInstructionsTests
{
    private readonly BuiltInInstructions instructions = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_WithoutUserInstructionsIsJustTheBuiltInOnes(string? user) => Assert.Equal(instructions.Compose(null, 5), instructions.Compose(user, 5));

    [Fact]
    public void Compose_PutsTheBuiltInInstructionsInFrontOfTheTrimmedUserInstructions() => Assert.EndsWith("\n\nbe brief", instructions.Compose("  be brief \n", 5));

    [Fact]
    public void Compose_DoesNotDoubleTheBuiltInInstructionsAUserTextAlreadyStartsWithAndReplacesThemWithTheCurrentInterval()
    {
        string composed = instructions.Compose("more", 5);

        Assert.Equal(instructions.Compose(null, 5), instructions.Compose(instructions.Compose(null, 5), 5));
        Assert.Equal(instructions.Compose("more", 10), instructions.Compose(composed, 10));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("be brief", "be brief")]
    [InlineData("  be brief  ", "be brief")]
    public void Visible_LeavesWhatDoesNotStartWithTheBuiltInInstructions(string? stored, string? expected) => Assert.Equal(expected, instructions.Visible(stored));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(90)]
    public void Visible_TakesTheBuiltInInstructionsOffTheFrontWhateverTheInterval(int minutes)
    {
        Assert.Null(instructions.Visible(instructions.Compose(null, minutes)));
        Assert.Equal("be brief", instructions.Visible(instructions.Compose("be brief", minutes)));
    }

    [Fact]
    public void Visible_TakesTheOlderBuiltInWordingOffToo()
    {
        string earlier = "Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, write a short status update as a normal message, saying what you have done and what comes next, at least once every five minutes so the user can follow along; split the work so that no single step runs much longer than five minutes.";

        Assert.Null(instructions.Visible(earlier));
        Assert.Equal("be brief", instructions.Visible($"{earlier}\n\nbe brief"));
    }

    [Theory]
    [InlineData(1, "at least once every minute", 60)]
    [InlineData(5, "at least once every 5 minutes", 300)]
    [InlineData(30, "at least once every 30 minutes", 1800)]
    [InlineData(120, "at least once every 120 minutes", 3600)]
    public void Text_TellsTheAiToKeepItsTurnOpenRunLongWorkInTheBackgroundAndReportOnTheIntervalWhileWaiting(int minutes, string interval, int seconds)
    {
        string text = instructions.Text(minutes);

        Assert.Contains(interval, text);
        Assert.Contains("status update as a normal message", text);
        Assert.Contains("in the foreground", text);
        Assert.Contains("your turn stays open until all of the work is finished", text);
        Assert.Contains("may take more than about a minute in the background", text);
        Assert.Contains("background option of the shell and delegate tools", text);
        Assert.Contains("task_wait, task_output and task_stop", text);
        Assert.Contains($"task_wait with timeout_seconds {seconds}", text);
        Assert.Contains("wait for every background task and helper to end", text);
        Assert.Contains("never leave anything running after your reply", text);
        Assert.Contains("Never use scheduled or recurring work", text);
    }
}
