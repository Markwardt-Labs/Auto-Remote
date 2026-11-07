namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v10.0.21", 10, 0, 21, null)]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData(" 0.0.1 ", 0, 0, 1, null)]
    public void TryParse_ReadsVersions(string text, int major, int minor, int patch, string? suffix)
    {
        Assert.True(AppVersion.TryParse(text, out AppVersion? version));
        Assert.Equal(new AppVersion { Major = major, Minor = minor, Patch = patch, Suffix = suffix }, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3-")]
    [InlineData("99999999999.0.0")]
    public void TryParse_RejectsOtherText(string? text)
    {
        Assert.False(AppVersion.TryParse(text, out AppVersion? version));
        Assert.Null(version);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.2", 1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3-beta", 1)]
    [InlineData("1.2.3-beta", "1.2.3", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.2", "1.2.3", -1)]
    public void CompareTo_OrdersReleases(string left, string right, int expected)
    {
        Assert.True(AppVersion.TryParse(left, out AppVersion? first));
        Assert.True(AppVersion.TryParse(right, out AppVersion? second));

        Assert.Equal(expected, Math.Sign(first.CompareTo(second)));
        Assert.Equal(expected > 0, first > second);
        Assert.Equal(expected < 0, first < second);
    }

    [Fact]
    public void CompareTo_TreatsNothingAsEarlier() => Assert.Equal(1, new AppVersion { Major = 0, Minor = 0, Patch = 0 }.CompareTo(null));

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-rc.2")]
    public void ToString_WritesTheVersionBack(string text)
    {
        AppVersion.TryParse(text, out AppVersion? version);

        Assert.Equal(text, version!.ToString());
    }
}
