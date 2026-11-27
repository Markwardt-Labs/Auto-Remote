namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class SideSessionRepositoryTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly GitService git = new();

    private readonly SideSessionRepository repository;

    private readonly Guid parent = Guid.NewGuid();

    private readonly Guid id = Guid.NewGuid();

    public SideSessionRepositoryTests() => repository = new SideSessionRepository(git);

    public void Dispose() => temp.Dispose();

    private string Root => temp.Combine("sessions");

    private StoredSideSession Record(string? folder = null, string? root = null) => new() { ParentId = parent, Name = "side", Service = AiService.Local, State = SideSessionState.Ready, StartedFrom = SideSessionStart.Commit, Root = root ?? Root, Folder = folder ?? repository.Folder(root ?? Root, parent, id) };

    private string Fill(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "precious\n");
        return folder;
    }

    private async Task Refused(StoredSideSession record, string untouched, params string[] keep)
    {
        await Assert.ThrowsAsync<RequestException>(() => repository.Remove(string.Empty, id, record, keep));
        Assert.Throws<RequestException>(() => repository.RemoveIntegrationFiles(id, record, keep));
        Assert.True(File.Exists(Path.Combine(untouched, "keep.txt")));
    }

    [Fact]
    public async Task ARecordThatNamesAnyOtherFolderRemovesNothing()
    {
        string other = Fill(temp.Combine("project"));

        await Refused(Record(folder: other), other);
        await Refused(Record(folder: Fill(Path.Combine(Root, parent.ToString("N")))), Path.Combine(Root, parent.ToString("N")));
        await Refused(Record(folder: Fill(Path.Combine(Root, parent.ToString("N"), Guid.NewGuid().ToString("N")))), other);
        await Refused(Record(folder: other, root: string.Empty), other);
        await Refused(Record(folder: other, root: "relative"), other);
    }

    [Fact]
    public async Task AFolderThatHoldsSomethingElsesFilesIsLeftAlone()
    {
        string folder = Fill(repository.Folder(Root, parent, id));
        string inside = Fill(Path.Combine(folder, "workspace"));

        await Refused(Record(), folder, inside);
        await Refused(Record(), folder, folder);
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        await Refused(Record(), folder);
    }

    [Fact]
    public async Task ALinkThatLeadsOutOfTheStorageIsNeverFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string other = Fill(temp.Combine("project"));
        string folder = repository.Folder(Root, parent, id);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Directory.CreateSymbolicLink(folder, other);

        await Refused(Record(), other);
    }

    [Fact]
    public async Task ARecordedFolderIsRemovedWithItsIntegrationFilesEvenWhenTheRepositoryIsGone()
    {
        string folder = Fill(repository.Folder(Root, parent, id));
        string files = Fill(repository.IntegrationFolder(folder));
        string other = Fill(temp.Combine("project"));

        await repository.Remove(temp.Combine("gone"), id, Record(), [other]);

        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(files));
        Assert.False(Directory.Exists(Path.GetDirectoryName(folder)));
        Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
    }

    [Fact]
    public async Task RemovingASessionLeavesOtherBranchesAndWorktreesOfTheRepository()
    {
        string main = temp.Combine("main");
        Directory.CreateDirectory(main);
        await Git(main, "init", "--quiet", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(main, "a.txt"), "one\n");
        await Git(main, "add", "-A");
        await Git(main, "commit", "--quiet", "-m", "first");
        string mine = temp.Combine("my-worktree");
        await Git(main, "worktree", "add", "--quiet", "-b", "mine", mine);
        string folder = repository.Folder(Root, parent, id);
        await repository.Create(main, id, folder, SideSessionStart.Commit, CancellationToken.None);

        await repository.Remove(main, id, Record(), [main, mine]);

        Assert.Equal("main\nmine", await Git(main, "branch", "--format=%(refname:short)"));
        Assert.Equal(2, (await Git(main, "worktree", "list")).Split('\n').Length);
        Assert.True(File.Exists(Path.Combine(mine, "a.txt")));
    }

    private async Task<string> Git(string folder, params string[] arguments)
    {
        GitOutput result = await git.Run(folder, ["-c", "user.name=t", "-c", "user.email=t@t", .. arguments], CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        return result.Output.Trim();
    }
}
