namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class MessageTimeExtensionsTests
{
    private DateTimeOffset Local(int year, int month, int day, int hour, int minute) => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private string Stamp(DateTimeOffset time, DateTimeOffset now)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            return time.ToStamp(now);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ToStamp_ShowsOnlyTheTimeOfDayForTodayInTheLocalTimeZone()
    {
        DateTimeOffset now = Local(2026, 11, 5, 15, 30);

        Assert.Equal("09:05", Stamp(Local(2026, 11, 5, 9, 5).ToUniversalTime(), now));
        Assert.Equal("15:30", Stamp(now, now.ToUniversalTime()));
    }

    [Fact]
    public void ToStamp_SaysYesterdayForTheDayBeforeEvenWhenItIsJustAMinuteAgo()
    {
        Assert.Equal("Yesterday 23:59", Stamp(Local(2026, 11, 4, 23, 59), Local(2026, 11, 5, 0, 1)));
    }

    [Fact]
    public void ToStamp_AddsTheMonthAndDayWithinThisYearAndTheWholeDateForEarlierYears()
    {
        DateTimeOffset now = Local(2026, 11, 5, 15, 30);

        Assert.Equal("Mar 02, 09:05", Stamp(Local(2026, 3, 2, 9, 5), now));
        Assert.Equal("12/31/2025, 23:59", Stamp(Local(2025, 12, 31, 23, 59), now));
    }
}
