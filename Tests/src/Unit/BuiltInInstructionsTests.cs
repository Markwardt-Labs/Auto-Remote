namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class BuiltInInstructionsTests
{
    private readonly BuiltInInstructions instructions = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_WithoutUserInstructionsIsJustTheBuiltInOnes(string? user) => Assert.Equal(instructions.Text, instructions.Compose(user));

    [Fact]
    public void Compose_PutsTheBuiltInInstructionsInFrontOfTheTrimmedUserInstructions() => Assert.Equal($"{instructions.Text}\n\nbe brief", instructions.Compose("  be brief \n"));

    [Fact]
    public void Compose_DoesNotDoubleTheBuiltInInstructionsAUserTextAlreadyStartsWith()
    {
        Assert.Equal(instructions.Text, instructions.Compose(instructions.Text));
        Assert.Equal($"{instructions.Text}\n\nmore", instructions.Compose($"{instructions.Text}\n\nmore"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("be brief", "be brief")]
    [InlineData("  be brief  ", "be brief")]
    public void Visible_LeavesWhatDoesNotStartWithTheBuiltInInstructions(string? stored, string? expected) => Assert.Equal(expected, instructions.Visible(stored));

    [Fact]
    public void Visible_TakesTheBuiltInInstructionsOffTheFront()
    {
        Assert.Null(instructions.Visible(instructions.Text));
        Assert.Equal("be brief", instructions.Visible(instructions.Compose("be brief")));
    }

    [Fact]
    public void Text_KeepsTheWorkInTheForeground() => Assert.Contains("in the foreground", instructions.Text);
}
