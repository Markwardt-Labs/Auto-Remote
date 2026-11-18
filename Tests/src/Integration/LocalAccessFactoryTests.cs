namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class LocalAccessFactoryTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose() => temp.Dispose();

    private ILocalAccess Create(string passwd, string? logins, IReadOnlyList<string>? users)
    {
        string passwdFile = temp.Combine("passwd");
        string loginsFile = temp.Combine("login.defs");
        File.WriteAllText(passwdFile, passwd);

        if (logins is not null)
        {
            File.WriteAllText(loginsFile, logins);
        }

        return new ILocalAccess.Factory(passwdFile, loginsFile).Create(users);
    }

    [Fact]
    public void Create_ReadsTheAccountsAndTheMinimumOfTheRegularOnes()
    {
        ILocalAccess access = Create("root:x:0:0:root:/root:/bin/bash\nalice:x:1500:1500::/home/alice:/bin/bash\nbroken\nbob:x:x:1::/:/bin/sh\n", "# comment\nUID_MIN\t\t1500\nUID_MAX 60000\n", null);

        Assert.True(access.Allows(1500));
        Assert.False(access.Allows(1499));
    }

    [Fact]
    public void Create_ResolvesTheNamedAccountsAndIgnoresUnknownOnes()
    {
        ILocalAccess access = Create("root:x:0:0::/root:/bin/bash\nalice:x:1000:1000::/home/alice:/bin/bash\n", null, ["root", "ghost"]);

        Assert.True(access.Allows(0));
        Assert.False(access.Allows(1000));
    }

    [Fact]
    public void Create_WithoutTheSystemFilesStillAppliesTheUsualMinimum()
    {
        ILocalAccess access = new ILocalAccess.Factory(temp.Combine("missing-passwd"), temp.Combine("missing-defs")).Create(null);

        Assert.True(access.Allows(1000));
        Assert.False(access.Allows(999));
    }
}
