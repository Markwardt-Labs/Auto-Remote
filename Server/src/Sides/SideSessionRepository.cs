namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The files and git side of side sessions: a worktree of the main repository on a branch of its own, the snapshot it started from, what changed since, and its removal, which only ever touches what the server recorded making.
/// </summary>
internal interface ISideSessionRepository
{
    /// <summary>
    /// Gets what the branches of side sessions start with.
    /// </summary>
    string BranchPrefix { get; }

    /// <summary>
    /// Gets the branch of a side session.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <returns>The branch name.</returns>
    string Branch(Guid id);

    /// <summary>
    /// Gets the folder of a side session in a storage folder.
    /// </summary>
    /// <param name="root">The folder side sessions are stored in.</param>
    /// <param name="parentId">The main workspace.</param>
    /// <param name="id">The side session.</param>
    /// <returns>The folder.</returns>
    string Folder(string root, Guid parentId, Guid id);

    /// <summary>
    /// Refuses a folder that cannot have side sessions: it has to be the top of a git repository with a commit.
    /// </summary>
    /// <param name="folder">The main workspace folder.</param>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns>A task that completes when the folder is fine.</returns>
    /// <exception cref="RequestException">It is not.</exception>
    Task RequireRepository(string folder, CancellationToken cancellation);

    /// <summary>
    /// Makes the worktree of a side session and records the snapshot it starts from.
    /// </summary>
    /// <param name="main">The main workspace folder.</param>
    /// <param name="id">The side session.</param>
    /// <param name="folder">The folder to make, which must not exist.</param>
    /// <param name="start">What the files start from.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The commit main had checked out and the tree of the files the session starts with.</returns>
    /// <exception cref="RequestException">Git could not make it.</exception>
    Task<(string Commit, string Tree)> Create(string main, Guid id, string folder, SideSessionStart start, CancellationToken cancellation);

    /// <summary>
    /// Makes the worktree of a side session that goes on with the result of a delegated task: on the commit that was checked out when the task was delegated, with the files as the task left them, and what the task started from recorded as the snapshot.
    /// </summary>
    /// <param name="main">The main workspace folder.</param>
    /// <param name="id">The side session.</param>
    /// <param name="folder">The folder to make, which must not exist.</param>
    /// <param name="baseCommit">The commit the task started from.</param>
    /// <param name="resultCommit">The commit holding the files as the task left them.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The commit the session is on and the tree its changes are measured against.</returns>
    /// <exception cref="RequestException">Git could not make it.</exception>
    Task<(string Commit, string Tree)> CreateFrom(string main, Guid id, string folder, string baseCommit, string resultCommit, CancellationToken cancellation);

    /// <summary>
    /// Lists what changed between the snapshot a session started from and its files as they are.
    /// </summary>
    /// <param name="folder">The session folder.</param>
    /// <param name="tree">The snapshot.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The changed files.</returns>
    Task<GitChanges> Changes(string folder, string tree, CancellationToken cancellation);

    /// <summary>
    /// Compares one file with how it was when the session started.
    /// </summary>
    /// <param name="folder">The session folder.</param>
    /// <param name="tree">The snapshot.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The comparison.</returns>
    Task<GitDiff> Diff(string folder, string tree, string path, CancellationToken cancellation);

    /// <summary>
    /// Writes the change list and the diff of a session for the main AI to read.
    /// </summary>
    /// <param name="folder">The session folder.</param>
    /// <param name="tree">The snapshot it started from.</param>
    /// <param name="into">The folder to write them in, which is made anew.</param>
    /// <param name="handoff">What the session's AI said about its work.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The change list as written.</returns>
    Task<string> Export(string folder, string tree, string into, string handoff, CancellationToken cancellation);

    /// <summary>
    /// Tells whether a repository holds the starting files recorded for side sessions, which only the one they were made from does.
    /// </summary>
    /// <param name="folder">The folder of the repository.</param>
    /// <param name="ids">The side sessions.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns><see langword="true" /> if it holds them all.</returns>
    Task<bool> Holds(string folder, IReadOnlyList<Guid> ids, CancellationToken cancellation);

    /// <summary>
    /// Brings the links between a main repository and the worktrees of its side sessions up to date after the main folder moved.
    /// </summary>
    /// <param name="main">The main workspace folder where it is now.</param>
    /// <param name="folders">The session folders.</param>
    /// <returns>A task that completes once done.</returns>
    Task Repair(string main, IReadOnlyList<string> folders);

    /// <summary>
    /// Removes the worktree, the integration files, the branch and the snapshot of a side session. Only the folder that follows from the recorded storage folder and the identifiers is removed, never one that holds a repository of its own or a workspace.
    /// </summary>
    /// <param name="main">The main workspace folder.</param>
    /// <param name="id">The side session.</param>
    /// <param name="side">What was recorded about it.</param>
    /// <param name="keep">Folders that must never be removed or lose anything: the workspaces and the user's home.</param>
    /// <returns>A task that completes once done.</returns>
    /// <exception cref="RequestException">The recorded folder is not one the server may remove.</exception>
    Task Remove(string main, Guid id, StoredSideSession side, IReadOnlyList<string> keep);

    /// <summary>
    /// Gets the folder of the integration files of a side session, next to its folder.
    /// </summary>
    /// <param name="folder">The session folder.</param>
    /// <returns>The folder.</returns>
    string IntegrationFolder(string folder);

    /// <summary>
    /// Removes the integration files of a side session.
    /// </summary>
    /// <param name="id">The side session.</param>
    /// <param name="side">What was recorded about it.</param>
    /// <param name="keep">Folders that must never be removed.</param>
    void RemoveIntegrationFiles(Guid id, StoredSideSession side, IReadOnlyList<string> keep);
}

/// <inheritdoc />
internal sealed class SideSessionRepository(IGitService git) : ISideSessionRepository
{
    private readonly Dictionary<string, string> identity = new()
    {
        ["GIT_AUTHOR_NAME"] = "Auto Remote",
        ["GIT_AUTHOR_EMAIL"] = "auto-remote@localhost",
        ["GIT_COMMITTER_NAME"] = "Auto Remote",
        ["GIT_COMMITTER_EMAIL"] = "auto-remote@localhost"
    };

    private readonly TimeSpan checkoutTimeout = TimeSpan.FromMinutes(30);

    private readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public string BranchPrefix => "auto-remote/session/";

    /// <inheritdoc />
    public string Branch(Guid id) => $"{BranchPrefix}{id:N}";

    /// <inheritdoc />
    public string Folder(string root, Guid parentId, Guid id) => Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), parentId.ToString("N"), id.ToString("N"));

    /// <inheritdoc />
    public string IntegrationFolder(string folder) => folder + ".integration";

    /// <inheritdoc />
    public async Task RequireRepository(string folder, CancellationToken cancellation)
    {
        GitOutput top = await git.Run(folder, ["rev-parse", "--show-toplevel"], cancellation);

        if (top.ExitCode != 0 || !Same(Resolve(top.Output.Trim()), Resolve(folder)))
        {
            throw new RequestException("Side sessions need the workspace to be the top folder of a git repository.");
        }

        if ((await git.Run(folder, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellation)).ExitCode != 0)
        {
            throw new RequestException("Side sessions need the repository to have a commit.");
        }
    }

    /// <inheritdoc />
    public async Task<(string Commit, string Tree)> Create(string main, Guid id, string folder, SideSessionStart start, CancellationToken cancellation)
    {
        string commit = await Output(main, ["rev-parse", "--verify", "HEAD"], cancellation);
        // Taken before the worktree is made, so what main does meanwhile is not mixed in.
        string tree = start == SideSessionStart.Files ? await WorkingTree(main, cancellation) : await Output(main, ["rev-parse", $"{commit}^{{tree}}"], cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Check(await git.Run(main, ["worktree", "add", "-b", Branch(id), folder, commit], cancellation, checkoutTimeout), "The files of the session could not be made");

        // With nothing uncommitted there is nothing to bring over, which git also refuses to do in a repository without a file.
        if (start == SideSessionStart.Files && tree != await Output(main, ["rev-parse", $"{commit}^{{tree}}"], cancellation))
        {
            // Only the files change, so main's uncommitted changes are uncommitted in the session too.
            Check(await git.Run(folder, ["restore", $"--source={tree}", "--worktree", "--no-overlay", "--", "."], cancellation, checkoutTimeout), "Main's files could not be brought into the session");
        }

        await Record(main, id, tree, commit, cancellation);
        return (commit, tree);
    }

    /// <inheritdoc />
    public async Task<(string Commit, string Tree)> CreateFrom(string main, Guid id, string folder, string baseCommit, string resultCommit, CancellationToken cancellation)
    {
        string commit = await Output(main, ["rev-parse", "--verify", $"{baseCommit}^{{commit}}"], cancellation);

        // The commits the swarm made to hold uncommitted files, for any of its versions, are no history of the project.
        while ((await Output(main, ["log", "-1", "--format=%an%n%s", commit], cancellation)).Split('\n') is ["Auto Remote", var subject] && subject.EndsWith("(Auto Remote)", StringComparison.Ordinal))
        {
            commit = await Output(main, ["rev-parse", "--verify", $"{commit}^1"], cancellation);
        }

        string tree = await Output(main, ["rev-parse", $"{baseCommit}^{{tree}}"], cancellation);
        string result = await Output(main, ["rev-parse", $"{resultCommit}^{{tree}}"], cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Check(await git.Run(main, ["worktree", "add", "-b", Branch(id), folder, commit], cancellation, checkoutTimeout), "The files of the session could not be made");

        if (result != await Output(main, ["rev-parse", $"{commit}^{{tree}}"], cancellation))
        {
            Check(await git.Run(folder, ["restore", $"--source={result}", "--worktree", "--no-overlay", "--", "."], cancellation, checkoutTimeout), "The files of the task could not be brought into the session");
        }

        await Record(main, id, tree, commit, cancellation);
        return (commit, tree);
    }

    /// <inheritdoc />
    public async Task<GitChanges> Changes(string folder, string tree, CancellationToken cancellation)
    {
        string now = await WorkingTree(folder, cancellation);
        string[] parts = (await Raw(folder, ["diff", "--name-status", "--no-renames", "-z", tree, now, "--"], cancellation)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<GitChange> changes = [];

        for (int index = 0; index + 1 < parts.Length; index += 2)
        {
            changes.Add(new GitChange { Path = parts[index + 1], Status = parts[index] switch { "A" => GitChangeStatus.Added, "D" => GitChangeStatus.Deleted, _ => GitChangeStatus.Modified } });
        }

        return new GitChanges { Changes = changes };
    }

    /// <inheritdoc />
    public async Task<GitDiff> Diff(string folder, string tree, string path, CancellationToken cancellation)
        => await git.DiffTrees(folder, path, tree, await WorkingTree(folder, cancellation), cancellation);

    /// <inheritdoc />
    public async Task<string> Export(string folder, string tree, string into, string handoff, CancellationToken cancellation)
    {
        string now = await WorkingTree(folder, cancellation);
        string list = await Raw(folder, ["-c", "core.quotePath=false", "diff", "--name-status", "-M", tree, now, "--"], cancellation);

        if (Directory.Exists(into))
        {
            Directory.Delete(into, true);
        }

        Directory.CreateDirectory(into);
        await File.WriteAllTextAsync(Path.Combine(into, "changes.txt"), list.Length > 0 ? list : "The session changed no files that git tracks or would track.\n", cancellation);
        // Written by git itself, which keeps the bytes of every file as they are.
        Check(await git.Run(folder, ["-c", "core.quotePath=false", "diff", "--binary", "-M", "--no-color", "--no-ext-diff", $"--output={Path.Combine(into, "changes.diff")}", tree, now, "--"], cancellation, checkoutTimeout), "The changes of the session could not be written");
        await File.WriteAllTextAsync(Path.Combine(into, "handoff.md"), handoff.TrimEnd() + "\n", cancellation);
        return list;
    }

    /// <inheritdoc />
    public async Task<bool> Holds(string folder, IReadOnlyList<Guid> ids, CancellationToken cancellation)
    {
        foreach (Guid id in ids)
        {
            if ((await git.Run(folder, ["rev-parse", "--verify", "--quiet", Snapshot(id)], cancellation)).ExitCode != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task Repair(string main, IReadOnlyList<string> folders)
    {
        if (folders.Count > 0)
        {
            await git.Run(main, ["worktree", "repair", .. folders.Where(Directory.Exists)], CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task Remove(string main, Guid id, StoredSideSession side, IReadOnlyList<string> keep)
    {
        string folder = Removable(id, side, keep);
        RemoveIntegrationFiles(id, side, keep);
        bool repository = Directory.Exists(main);

        if (repository && Directory.Exists(folder))
        {
            await git.Run(main, ["worktree", "remove", "--force", "--force", folder], CancellationToken.None, checkoutTimeout);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        if (repository)
        {
            // With the folder gone this drops git's record of the worktree, and only of this one.
            await git.Run(main, ["worktree", "remove", "--force", "--force", folder], CancellationToken.None);
            await git.Run(main, ["branch", "-D", Branch(id)], CancellationToken.None);
            await git.Run(main, ["update-ref", "-d", Snapshot(id)], CancellationToken.None);
        }

        if (Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    /// <inheritdoc />
    public void RemoveIntegrationFiles(Guid id, StoredSideSession side, IReadOnlyList<string> keep)
    {
        string files = IntegrationFolder(Removable(id, side, keep));

        if (Directory.Exists(files) && new DirectoryInfo(files).LinkTarget is null)
        {
            Directory.Delete(files, true);
        }
    }

    private async Task Record(string main, Guid id, string tree, string commit, CancellationToken cancellation)
    {
        GitOutput kept = await git.Run(main, ["commit-tree", tree, "-p", commit, "-m", "Auto Remote: the files a side session started with"], identity, cancellation);
        Check(kept, "The starting snapshot could not be recorded");
        Check(await git.Run(main, ["update-ref", Snapshot(id), kept.Output.Trim()], cancellation), "The starting snapshot could not be recorded");
    }

    private string Snapshot(Guid id) => $"refs/auto-remote/session/{id:N}/base";

    // The records could be damaged or made by hand, so the folder is only trusted when it is exactly where the server puts one, and holds nothing that is someone else's.
    private string Removable(Guid id, StoredSideSession side, IReadOnlyList<string> keep)
    {
        if (side.Root.Length == 0 || !Path.IsPathFullyQualified(side.Root) || !Same(side.Folder, Folder(side.Root, side.ParentId, id)))
        {
            throw new RequestException($"The folder recorded for the side session '{side.Name}' is not where the server keeps one, so nothing was removed.");
        }

        string folder = side.Folder;
        string resolved = Resolve(folder);

        if (!Inside(resolved, Resolve(side.Root)) || keep.Any(kept => Inside(Resolve(kept), resolved)))
        {
            throw new RequestException($"Removing the folder of the side session '{side.Name}' would remove files outside the side session storage, so nothing was removed.");
        }

        if (Directory.Exists(Path.Combine(folder, ".git")))
        {
            throw new RequestException($"The folder of the side session '{side.Name}' holds a git repository of its own, which the server did not make, so nothing was removed.");
        }

        return folder;
    }

    private string Resolve(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string current = Path.GetPathRoot(full)!;

        foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);

            if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    private bool Same(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), comparison);

    private bool Inside(string child, string parent) => Same(child, parent) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, comparison);

    // The tree of the files as they are, new ones included and ignored ones left out, made in a copy of the index so the real one stays as it is.
    private async Task<string> WorkingTree(string folder, CancellationToken cancellation)
    {
        string index = await Output(folder, ["rev-parse", "--path-format=absolute", "--git-path", "index"], cancellation);
        string copy = Path.Combine(Path.GetTempPath(), "autoremote-side-index-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (File.Exists(index))
            {
                File.Copy(index, copy);
            }

            Dictionary<string, string> environment = new() { ["GIT_INDEX_FILE"] = copy };
            Check(await git.Run(folder, ["add", "-A"], environment, cancellation, checkoutTimeout), "The files could not be read");
            GitOutput written = await git.Run(folder, ["write-tree"], environment, cancellation);
            Check(written, "The files could not be recorded");
            return written.Output.Trim();
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private async Task<string> Output(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
        => (await Raw(folder, arguments, cancellation)).Trim();

    private async Task<string> Raw(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, arguments, cancellation);
        Check(result, "Git failed");
        return result.Output;
    }

    private void Check(GitOutput result, string what)
    {
        if (result.ExitCode != 0)
        {
            string reason = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
            throw new RequestException(reason.Length > 0 ? $"{what}: {reason}" : $"{what}.");
        }
    }
}
