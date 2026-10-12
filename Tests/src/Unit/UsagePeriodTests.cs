namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class UsagePeriodTests
{
    private readonly DateTimeOffset now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private UsagePeriod Session(double used, TimeSpan? resetsIn = null) => new() { PercentUsed = used, WindowMinutes = 300, ResetsAt = resetsIn is { } left ? now + left : null };

    [Theory]
    [InlineData(34, UsagePace.VeryLow)]
    [InlineData(36, UsagePace.Low)]
    [InlineData(44, UsagePace.Low)]
    [InlineData(46, UsagePace.Even)]
    [InlineData(55, UsagePace.Even)]
    [InlineData(56, UsagePace.High)]
    [InlineData(65, UsagePace.High)]
    [InlineData(66, UsagePace.VeryHigh)]
    public void Pace_ComparesPercentUsedWithPercentOfTheWindowPassed(double used, UsagePace expected) => Assert.Equal(expected, Session(used, TimeSpan.FromMinutes(150)).Pace(now));

    [Fact]
    public void Pace_IsVeryHighOnceTheLimitIsUsedUp() => Assert.Equal(UsagePace.VeryHigh, Session(100, TimeSpan.FromMinutes(299)).Pace(now));

    [Fact]
    public void Pace_IsVeryLowWhenNothingIsUsedWellIntoTheWindow() => Assert.Equal(UsagePace.VeryLow, Session(0, TimeSpan.FromMinutes(100)).Pace(now));

    [Theory]
    [InlineData(null)]
    [InlineData(-5)]
    public void Pace_IsEvenWhenTheResetTimeIsUnknownOrPassed(int? minutes) => Assert.Equal(UsagePace.Even, Session(80, minutes is { } value ? TimeSpan.FromMinutes(value) : null).Pace(now));
}
