namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class WorkspaceSnapshotsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly WorkspaceSnapshots snapshots = new(new GitService());

    private readonly string work;

    private readonly WorkspaceInfo workspace;

    public WorkspaceSnapshotsTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        work = temp.Combine("work");
        Directory.CreateDirectory(work);
        G("init", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(work, "b.txt"), "bee\n");
        G("add", "-A");
        G("commit", "-m", "base");
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = work };
    }

    public void Dispose() => temp.Dispose();

    private string G(params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(work, name));

    private void Write(string name, string text)
    {
        string path = Path.Combine(work, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Revert_PutsBackEditedDeletedAndAddedFilesCommitsBranchesAndTheIndex()
    {
        Write("a.txt", "one edited before\n");
        Write("scratch/mine.txt", "untracked and mine\n");
        G("add", "b.txt");
        string head = G("rev-parse", "HEAD");
        string status = G("status", "--porcelain");
        await snapshots.Capture(workspace, CancellationToken.None);
        Assert.True(snapshots.Has(workspace.Id));

        Write("a.txt", "one edited by the AI\n");
        File.Delete(Path.Combine(work, "b.txt"));
        Write("scratch/mine.txt", "changed by the AI\n");
        Write("deep/er/new.txt", "created by the AI\n");
        G("add", "-A");
        G("commit", "-m", "ai commit");
        G("checkout", "-b", "ai-branch");
        Write("later.txt", "after the commit\n");

        await snapshots.Revert(workspace.Id);

        Assert.False(snapshots.Has(workspace.Id));
        Assert.Equal("one edited before\n", Read("a.txt"));
        Assert.Equal("bee\n", Read("b.txt"));
        Assert.Equal("untracked and mine\n", Read("scratch/mine.txt"));
        Assert.False(File.Exists(Path.Combine(work, "later.txt")));
        Assert.False(Directory.Exists(Path.Combine(work, "deep")));
        Assert.Equal("main", G("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(head, G("rev-parse", "HEAD"));
        Assert.Equal(status, G("status", "--porcelain"));
    }

    [Fact]
    public async Task Capture_DoesNotChangeTheIndexOrTheFiles()
    {
        Write("a.txt", "edited\n");
        Write("new.txt", "new\n");
        G("add", "a.txt");
        string status = G("status", "--porcelain");

        await snapshots.Capture(workspace, CancellationToken.None);

        Assert.Equal(status, G("status", "--porcelain"));
        Assert.Equal("edited\n", Read("a.txt"));
    }

    [Fact]
    public async Task Capture_KeepsNothingForAFolderThatIsNotARepositoryRoot()
    {
        string plain = temp.Combine("plain");
        Directory.CreateDirectory(plain);
        string inside = Path.Combine(work, "sub");
        Directory.CreateDirectory(inside);

        await snapshots.Capture(new WorkspaceInfo { Id = Guid.NewGuid(), Folder = plain }, CancellationToken.None);
        WorkspaceInfo nested = new() { Id = Guid.NewGuid(), Folder = inside };
        await snapshots.Capture(nested, CancellationToken.None);

        Assert.False(snapshots.Has(nested.Id));
        await Assert.ThrowsAsync<RequestException>(() => snapshots.Revert(nested.Id));
    }

    [Fact]
    public async Task Discard_ForgetsTheSnapshot()
    {
        await snapshots.Capture(workspace, CancellationToken.None);

        snapshots.Discard(workspace.Id);

        Assert.False(snapshots.Has(workspace.Id));
    }
}
