namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class LocalAccessTests
{
    private readonly Dictionary<string, uint> accounts = new() { ["alice"] = 1000, ["bob"] = 1001, ["www-data"] = 33, ["root"] = 0 };

    private LocalAccess Create(IReadOnlyList<string>? users, uint minimum = 1000) => new(users, name => accounts.TryGetValue(name, out uint uid) ? uid : null, minimum);

    [Theory]
    [InlineData(1000u, true)]
    [InlineData(1001u, true)]
    [InlineData(50000u, true)]
    [InlineData(999u, false)]
    [InlineData(33u, false)]
    [InlineData(0u, false)]
    [InlineData(65534u, false)]
    [InlineData(4294967294u, false)]
    public void Allows_WithoutAListAdmitsRegularUserAccountsOnly(uint uid, bool expected)
    {
        LocalAccess access = Create(null);

        Assert.True(access.IsEnabled);
        Assert.Equal(expected, access.Allows(uid));
    }

    [Fact]
    public void Allows_WithoutAListFollowsTheMinimumOfTheSystem()
    {
        LocalAccess access = Create(null, 500);

        Assert.True(access.Allows(500));
        Assert.False(access.Allows(499));
    }

    [Fact]
    public void IsEnabled_IsFalseForAnEmptyListWhichAdmitsNobody()
    {
        LocalAccess access = Create([]);

        Assert.False(access.IsEnabled);
        Assert.False(access.Allows(1000));
        Assert.False(access.Allows(0));
    }

    [Fact]
    public void Allows_WithAListAdmitsExactlyTheNamedAccountsEvenIfTheyAreSystemAccounts()
    {
        LocalAccess access = Create(["alice", " root ", "nobody-here"]);

        Assert.True(access.IsEnabled);
        Assert.True(access.Allows(1000));
        Assert.True(access.Allows(0));
        Assert.False(access.Allows(1001));
        Assert.False(access.Allows(33));
    }

    [Fact]
    public void Allows_WithAListOfUnknownAccountsAdmitsNobody()
    {
        LocalAccess access = Create(["ghost"]);

        Assert.True(access.IsEnabled);
        Assert.False(access.Allows(1000));
    }
}
