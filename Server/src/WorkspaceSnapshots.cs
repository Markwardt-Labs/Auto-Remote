namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Remembers how a git repository workspace looked when work started in it, so cancelling that work can put the files back. A workspace that is not a git repository has no snapshot, and its work cannot be reverted.
/// </summary>
internal interface IWorkspaceSnapshots
{
    /// <summary>
    /// Checks whether a snapshot is kept for a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if work in the workspace can be reverted.</returns>
    bool Has(Guid id);

    /// <summary>
    /// Takes the snapshot of a workspace: its head, branch, index and the content of every file git does not ignore, untracked ones included. Does nothing when the folder is not the root of a git repository or git or the disk fails, so work never waits on or fails because of its snapshot.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the call.</param>
    Task Capture(WorkspaceInfo workspace, CancellationToken cancellation);

    /// <summary>
    /// Puts the files, the head, the branch and the index of a workspace back as the snapshot saw them, then forgets the snapshot. Files git ignores, other branches and anything pushed are not touched.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <exception cref="RequestException">There is no snapshot, or git could not restore it.</exception>
    Task Revert(Guid id);

    /// <summary>
    /// Forgets the snapshot of a workspace; does nothing when there is none.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Discard(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceSnapshots(IGitService git) : IWorkspaceSnapshots
{
    private readonly ConcurrentDictionary<Guid, Snapshot> snapshots = new();

    private readonly int batch = 100;

    /// <inheritdoc />
    public bool Has(Guid id) => snapshots.ContainsKey(id);

    /// <inheritdoc />
    public async Task Capture(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        snapshots.TryRemove(workspace.Id, out _);

        try
        {
            string folder = workspace.Folder;

            if (await Text(folder, ["rev-parse", "--show-toplevel"], cancellation) is not { Length: > 0 } top || !SameFolder(top, folder))
            {
                return;
            }

            string? indexTree = await Text(folder, ["write-tree"], cancellation);
            string? tree = await WorkingTree(folder, cancellation);

            if (indexTree is null || tree is null)
            {
                return;
            }

            snapshots[workspace.Id] = new Snapshot(folder, await Text(folder, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellation), await Text(folder, ["symbolic-ref", "--quiet", "HEAD"], cancellation), indexTree, tree);
        }
        catch (Exception exception) when (exception is RequestException or IOException or UnauthorizedAccessException or Win32Exception)
        {
        }
    }

    /// <inheritdoc />
    public async Task Revert(Guid id)
    {
        if (!snapshots.TryRemove(id, out Snapshot? snapshot))
        {
            throw new RequestException("This workspace has nothing to revert to.");
        }

        string folder = snapshot.Folder;
        string current = await WorkingTree(folder, CancellationToken.None) ?? throw new RequestException("Git could not read the workspace to revert it.");
        Dictionary<string, string> literal = new() { ["GIT_LITERAL_PATHSPECS"] = "1" };
        string[] changes = (await Run(folder, ["diff", "--name-status", "-z", "--no-renames", snapshot.Tree, current], literal)).Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<string> restore = [];

        for (int index = 0; index + 1 < changes.Length; index += 2)
        {
            if (changes[index] == "A")
            {
                Remove(folder, changes[index + 1]);
            }
            else
            {
                restore.Add(changes[index + 1]);
            }
        }

        foreach (string[] paths in restore.Chunk(batch))
        {
            Check(await Run(folder, ["restore", $"--source={snapshot.Tree}", "--worktree", "--no-overlay", "--", .. paths], literal));
        }

        if (snapshot.Head is not null)
        {
            if (snapshot.Branch is not null)
            {
                Check(await Run(folder, ["symbolic-ref", "HEAD", snapshot.Branch], literal));
                Check(await Run(folder, ["update-ref", snapshot.Branch, snapshot.Head], literal));
            }
            else
            {
                Check(await Run(folder, ["update-ref", "--no-deref", "HEAD", snapshot.Head], literal));
            }
        }

        Check(await Run(folder, ["read-tree", snapshot.IndexTree], literal));
    }

    /// <inheritdoc />
    public void Discard(Guid id) => snapshots.TryRemove(id, out _);

    private bool SameFolder(string top, string folder) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<string?> WorkingTree(string folder, CancellationToken cancellation)
    {
        string index = (await Text(folder, ["rev-parse", "--path-format=absolute", "--git-path", "index"], cancellation)) ?? string.Empty;
        string copy = Path.Combine(Path.GetTempPath(), "autoremote-index-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (index.Length > 0 && File.Exists(index))
            {
                File.Copy(index, copy);
            }

            Dictionary<string, string> environment = new() { ["GIT_INDEX_FILE"] = copy };

            if ((await git.Run(folder, ["add", "-A"], environment, cancellation)).ExitCode != 0)
            {
                return null;
            }

            GitOutput written = await git.Run(folder, ["write-tree"], environment, cancellation);
            return written.ExitCode == 0 && written.Output.Trim() is { Length: > 0 } tree ? tree : null;
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private void Remove(string folder, string relative)
    {
        string root = Path.GetFullPath(folder);
        string full = Path.GetFullPath(Path.Combine(root, relative));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return;
        }

        File.Delete(full);

        for (string? parent = Path.GetDirectoryName(full); parent is not null && parent.Length > root.Length && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any(); parent = Path.GetDirectoryName(parent))
        {
            Directory.Delete(parent);
        }
    }

    private async Task<string?> Text(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, arguments, cancellation);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } text ? text : null;
    }

    private Task<GitOutput> Run(string folder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) => git.Run(folder, arguments, environment, CancellationToken.None);

    private GitOutput Check(GitOutput result) => result.ExitCode == 0 ? result : throw new RequestException((result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim() is { Length: > 0 } text ? text : "Git failed.");

    private sealed record Snapshot(string Folder, string? Head, string? Branch, string IndexTree, string Tree);
}
