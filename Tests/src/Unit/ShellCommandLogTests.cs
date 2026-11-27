namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ShellCommandLogTests
{
    private readonly ShellCommandLog log = new();

    [Fact]
    public void Find_ListsTheSessionsThatRanACommandTheLatestFirstEachOnce()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        log.Note(first, "dotnet build");
        log.Note(second, "dotnet build");
        log.Note(first, "dotnet build");
        log.Note(second, "ls");

        Assert.Equal([first, second], log.Find("dotnet build"));
        Assert.Equal([second], log.Find("ls"));
        Assert.Empty(log.Find("make"));
    }

    [Fact]
    public void Note_ForgetsTheOldestCommandsOnceThereAreMany()
    {
        Guid session = Guid.NewGuid();

        for (int index = 0; index <= 500; index++)
        {
            log.Note(session, $"command {index}");
        }

        Assert.Empty(log.Find("command 0"));
        Assert.Equal([session], log.Find("command 1"));
        Assert.Equal([session], log.Find("command 500"));
    }
}
