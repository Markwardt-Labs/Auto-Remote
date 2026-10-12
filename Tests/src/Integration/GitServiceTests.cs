namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class GitServiceTests
{
    private readonly GitService git = new();

    public GitServiceTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
    }

    private void Run(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private void Write(TempFolder temp, string name, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Combine(name))!);
        File.WriteAllText(temp.Combine(name), text);
    }

    private async Task<TempFolder> Repo()
    {
        TempFolder temp = new();
        await git.SetRemote(temp.Path, "/nonexistent/app.git", CancellationToken.None);
        Write(temp, "a.txt", "one\ntwo\nthree\n");
        Write(temp, "src/b.txt", "b\n");
        await git.Commit(temp.Path, "first", CancellationToken.None);
        return temp;
    }

    [Fact]
    public async Task Status_ReportsNotARepositoryForAPlainFolder()
    {
        using TempFolder temp = new();

        GitStatus status = await git.Status(temp.Path, CancellationToken.None);

        Assert.False(status.IsRepo);
    }

    [Fact]
    public async Task Status_ReportsNotARepositoryForAFolderInsideOne()
    {
        using TempFolder temp = await Repo();
        Directory.CreateDirectory(temp.Combine("inner"));

        GitStatus status = await git.Status(temp.Combine("inner"), CancellationToken.None);

        Assert.False(status.IsRepo);
    }

    [Fact]
    public async Task SetRemote_CreatesTheRepositoryAndLaterRepointsTheRemote()
    {
        using TempFolder temp = new();

        await git.SetRemote(temp.Path, "/nonexistent/one.git", CancellationToken.None);
        GitStatus created = await git.Status(temp.Path, CancellationToken.None);
        await git.SetRemote(temp.Path, "/nonexistent/two.git", CancellationToken.None);
        GitStatus changed = await git.Status(temp.Path, CancellationToken.None);

        Assert.True(created.IsRepo);
        Assert.Equal("/nonexistent/one.git", created.Remote);
        Assert.Equal(string.Empty, created.Commit);
        Assert.Equal("/nonexistent/two.git", changed.Remote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--upload-pack=touch x")]
    public async Task SetRemote_RejectsUnusableUrls(string url)
    {
        using TempFolder temp = new();

        await Assert.ThrowsAsync<RequestException>(() => git.SetRemote(temp.Path, url, CancellationToken.None));
    }

    [Fact]
    public async Task Commit_RecordsEverythingAndReportsTheHead()
    {
        using TempFolder temp = await Repo();
        Write(temp, "c.txt", "new");

        GitStatus before = await git.Status(temp.Path, CancellationToken.None);
        await git.Commit(temp.Path, "second", CancellationToken.None);
        GitStatus after = await git.Status(temp.Path, CancellationToken.None);

        Assert.True(before.HasChanges);
        Assert.False(after.HasChanges);
        Assert.Equal("second", after.Message);
        Assert.Equal(7, after.Commit.Length);
        Assert.NotNull(after.Branch);
    }

    [Fact]
    public async Task Commit_RefusesWhenThereIsNothingToCommit()
    {
        using TempFolder temp = await Repo();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => git.Commit(temp.Path, "again", CancellationToken.None));

        Assert.Contains("nothing to commit", exception.Message);
    }

    [Fact]
    public async Task Changes_ClassifiesAddedModifiedDeletedAndIgnored()
    {
        using TempFolder temp = await Repo();
        Write(temp, ".gitignore", "out/\n");
        await git.Commit(temp.Path, "ignore", CancellationToken.None);
        Write(temp, "a.txt", "changed\n");
        File.Delete(temp.Combine("src/b.txt"));
        Write(temp, "new/c.txt", "c");
        Write(temp, "out/x.bin", "x");

        IReadOnlyList<GitChange> changes = await git.Changes(temp.Path, CancellationToken.None);

        Assert.Equal(GitChangeStatus.Modified, changes.Single(change => change.Path == "a.txt").Status);
        Assert.Equal(GitChangeStatus.Deleted, changes.Single(change => change.Path == "src/b.txt").Status);
        Assert.Equal(GitChangeStatus.Added, changes.Single(change => change.Path == "new/c.txt").Status);
        Assert.Equal(GitChangeStatus.Ignored, changes.Single(change => change.Path == "out/").Status);
    }

    [Fact]
    public async Task Changes_ReportsARenameByItsNewPath()
    {
        using TempFolder temp = await Repo();
        Run(temp.Path, "mv", "a.txt", "renamed.txt");

        IReadOnlyList<GitChange> changes = await git.Changes(temp.Path, CancellationToken.None);

        Assert.Contains(changes, change => change.Path == "renamed.txt" && change.Status == GitChangeStatus.Modified);
        Assert.DoesNotContain(changes, change => change.Path == "a.txt" && change.Status != GitChangeStatus.Deleted);
    }

    [Fact]
    public async Task Diff_OfTheWorkingTreeMarksChangedLinesWithNumbers()
    {
        using TempFolder temp = await Repo();
        Write(temp, "a.txt", "one\nTWO\nthree\nfour\n");

        GitDiff diff = await git.Diff(temp.Path, "a.txt", null, CancellationToken.None);

        Assert.Equal(
            [(GitDiffKind.Unchanged, "one", 1, 1), (GitDiffKind.Removed, "two", 2, null), (GitDiffKind.Added, "TWO", null, 2), (GitDiffKind.Unchanged, "three", 3, 3), (GitDiffKind.Added, "four", null, 4)],
            diff.Lines.Select(line => (line.Kind, line.Text, line.OldNumber, line.NewNumber)));
    }

    [Fact]
    public async Task Diff_OfAnUntrackedFileIsEntirelyAdded()
    {
        using TempFolder temp = await Repo();
        Write(temp, "fresh.txt", "x\ny\n");

        GitDiff diff = await git.Diff(temp.Path, "fresh.txt", null, CancellationToken.None);

        Assert.All(diff.Lines, line => Assert.Equal(GitDiffKind.Added, line.Kind));
        Assert.Equal(["x", "y"], diff.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task Diff_OfADeletedFileIsEntirelyRemoved()
    {
        using TempFolder temp = await Repo();
        File.Delete(temp.Combine("src/b.txt"));

        GitDiff diff = await git.Diff(temp.Path, "src/b.txt", null, CancellationToken.None);

        Assert.Single(diff.Lines);
        Assert.Equal(GitDiffKind.Removed, diff.Lines[0].Kind);
    }

    [Fact]
    public async Task Diff_OfABinaryFileHasNoLines()
    {
        using TempFolder temp = await Repo();
        File.WriteAllBytes(temp.Combine("blob.bin"), [0, 1, 2, 0, 3]);

        GitDiff diff = await git.Diff(temp.Path, "blob.bin", null, CancellationToken.None);

        Assert.True(diff.IsBinary);
        Assert.Empty(diff.Lines);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task Diff_RejectsPathsOutsideTheRepository(string path)
    {
        using TempFolder temp = await Repo();

        await Assert.ThrowsAsync<RequestException>(() => git.Diff(temp.Path, path, null, CancellationToken.None));
    }

    [Fact]
    public async Task History_ListsCommitsNewestFirstWithBranchAndTagRefs()
    {
        using TempFolder temp = await Repo();
        Write(temp, "a.txt", "two\n");
        await git.Commit(temp.Path, "second", CancellationToken.None);
        await git.CreateTag(temp.Path, "v1", CancellationToken.None);

        GitHistory history = await git.History(temp.Path, 0, 10, CancellationToken.None);

        Assert.Equal(["second", "first"], history.Commits.Select(commit => commit.Subject));
        Assert.False(history.HasMore);
        Assert.Contains(history.Commits[0].Refs, item => item.Name == "v1" && item.Kind == GitRefKind.Tag);
        Assert.Contains(history.Commits[0].Refs, item => item.Kind == GitRefKind.Branch && item.IsCurrent);
        Assert.Equal("Tester", history.Commits[0].Author);
    }

    [Fact]
    public async Task History_PagesWithSkipAndLimit()
    {
        using TempFolder temp = await Repo();

        for (int i = 0; i < 4; i++)
        {
            Write(temp, "a.txt", $"{i}\n");
            await git.Commit(temp.Path, $"c{i}", CancellationToken.None);
        }

        GitHistory first = await git.History(temp.Path, 0, 2, CancellationToken.None);
        GitHistory second = await git.History(temp.Path, 2, 2, CancellationToken.None);
        GitHistory last = await git.History(temp.Path, 4, 2, CancellationToken.None);

        Assert.Equal(["c3", "c2"], first.Commits.Select(commit => commit.Subject));
        Assert.True(first.HasMore);
        Assert.Equal(["c1", "c0"], second.Commits.Select(commit => commit.Subject));
        Assert.True(second.HasMore);
        Assert.Equal(["first"], last.Commits.Select(commit => commit.Subject));
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task History_IsEmptyBeforeTheFirstCommit()
    {
        using TempFolder temp = new();
        await git.SetRemote(temp.Path, "/nonexistent/app.git", CancellationToken.None);

        GitHistory history = await git.History(temp.Path, 0, 10, CancellationToken.None);

        Assert.Empty(history.Commits);
    }

    [Fact]
    public async Task CommitChanges_ListsWhatACommitChangedAndDiffsAgainstItsParent()
    {
        using TempFolder temp = await Repo();
        Write(temp, "a.txt", "one\nchanged\nthree\n");
        File.Delete(temp.Combine("src/b.txt"));
        Write(temp, "z.txt", "z");
        await git.Commit(temp.Path, "second", CancellationToken.None);
        GitHistory history = await git.History(temp.Path, 0, 10, CancellationToken.None);

        IReadOnlyList<GitChange> latest = await git.CommitChanges(temp.Path, history.Commits[0].Hash, CancellationToken.None);
        IReadOnlyList<GitChange> root = await git.CommitChanges(temp.Path, history.Commits[1].Hash, CancellationToken.None);
        GitDiff diff = await git.Diff(temp.Path, "a.txt", history.Commits[0].Hash, CancellationToken.None);
        GitDiff rootDiff = await git.Diff(temp.Path, "a.txt", history.Commits[1].Hash, CancellationToken.None);

        Assert.Equal([("a.txt", GitChangeStatus.Modified), ("src/b.txt", GitChangeStatus.Deleted), ("z.txt", GitChangeStatus.Added)], latest.Select(change => (change.Path, change.Status)));
        Assert.Equal([("a.txt", GitChangeStatus.Added), ("src/b.txt", GitChangeStatus.Added)], root.Select(change => (change.Path, change.Status)));
        Assert.Contains(diff.Lines, line => line.Kind == GitDiffKind.Added && line.Text == "changed");
        Assert.All(rootDiff.Lines, line => Assert.Equal(GitDiffKind.Added, line.Kind));
    }

    [Theory]
    [InlineData("--output=x")]
    [InlineData("HEAD")]
    public async Task CommitChanges_OnlyAcceptsHashes(string commit)
    {
        using TempFolder temp = await Repo();

        await Assert.ThrowsAsync<RequestException>(() => git.CommitChanges(temp.Path, commit, CancellationToken.None));
    }

    [Fact]
    public async Task Reset_DiscardsChangesAndNewFiles()
    {
        using TempFolder temp = await Repo();
        Write(temp, "a.txt", "dirty");
        Write(temp, "new.txt", "new");

        await git.Reset(temp.Path, CancellationToken.None);

        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(temp.Combine("a.txt")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(temp.Combine("new.txt")));
        Assert.False((await git.Status(temp.Path, CancellationToken.None)).HasChanges);
    }

    [Fact]
    public async Task CreateBranch_ChecksTheNewBranchOutAndRejectsBadOrTakenNames()
    {
        using TempFolder temp = await Repo();

        await git.CreateBranch(temp.Path, "feature/x", CancellationToken.None);

        Assert.Equal("feature/x", (await git.Status(temp.Path, CancellationToken.None)).Branch);
        await Assert.ThrowsAsync<RequestException>(() => git.CreateBranch(temp.Path, "feature/x", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => git.CreateBranch(temp.Path, "bad name", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => git.CreateBranch(temp.Path, "-b", CancellationToken.None));
    }

    [Fact]
    public async Task CreateTag_TagsTheHeadAndRejectsBadOrTakenNames()
    {
        using TempFolder temp = await Repo();

        await git.CreateTag(temp.Path, "v1", CancellationToken.None);

        Assert.Equal("v1", (await git.Status(temp.Path, CancellationToken.None)).Tag);
        await Assert.ThrowsAsync<RequestException>(() => git.CreateTag(temp.Path, "v1", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => git.CreateTag(temp.Path, "bad name", CancellationToken.None));
    }

    [Fact]
    public async Task Clone_CopiesARepositoryIntoANewFolderNamedAfterIt()
    {
        using TempFolder source = await Repo();
        using TempFolder parent = new();

        string cloned = await git.Clone(parent.Path, source.Path, "copy", CancellationToken.None);
        GitStatus status = await git.Status(cloned, CancellationToken.None);

        Assert.Equal(parent.Combine("copy"), cloned);
        Assert.True(status.IsRepo);
        Assert.Equal("first", status.Message);
        Assert.Equal(source.Path, status.Remote);
    }

    [Fact]
    public async Task Clone_NamesTheFolderAfterTheUrlAndRefusesAnExistingFolderOrBadUrl()
    {
        using TempFolder source = await Repo();
        string url = source.Path;
        using TempFolder parent = new();
        Directory.CreateDirectory(parent.Combine("taken"));

        string cloned = await git.Clone(parent.Path, url, null, CancellationToken.None);

        Assert.Equal(parent.Combine(Path.GetFileName(source.Path)), cloned);
        await Assert.ThrowsAsync<RequestException>(() => git.Clone(parent.Path, url, "taken", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => git.Clone(parent.Path, "--upload-pack=x", "other", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => git.Clone(parent.Path, source.Combine("missing"), "gone", CancellationToken.None));
        Assert.False(Directory.Exists(parent.Combine("gone")));
    }
}
