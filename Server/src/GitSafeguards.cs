namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What keeps the git workflows from destroying work: noticing an operation that is already paused, finding files git does not track that a step would replace, keeping commits reachable before a branch moves and keeping files before a step discards them.
/// </summary>
internal interface IGitSafeguards
{
    /// <summary>
    /// Tells which operation of git is paused in a repository.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns><c>rebase</c>, <c>merge</c>, <c>cherry-pick</c> or <c>revert</c>, or <see langword="null" /> when none is.</returns>
    Task<string?> Operation(string folder, CancellationToken cancellation);

    /// <summary>
    /// Lists the files git does not track, ignored ones included, that would be replaced or removed by writing what commits hold. Git itself replaces ignored files without a word.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="tree">A commit whose whole content would be written; a file that already holds what it has there is not in the way.</param>
    /// <param name="range">Commits that would be replayed, as a range, or <see langword="null" />; every file any of them touches counts, whatever it holds.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The paths in the way.</returns>
    Task<IReadOnlyList<string>> InTheWay(string folder, string tree, string? range, CancellationToken cancellation);

    /// <summary>
    /// Keeps a commit reachable under a reference of its own, so a branch that is moved or deleted takes no commit with it. Only the newest of these are kept.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="name">What it was, such as the name of the branch.</param>
    /// <param name="commit">The commit; nothing happens when empty.</param>
    /// <returns>A task that completes once the reference exists.</returns>
    Task Keep(string folder, string name, string commit);

    /// <summary>
    /// Tells whether a file on disk holds what a commit has for it; a symbolic link is compared by where it points.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="commit">The commit, or empty for the index.</param>
    /// <param name="path">The file, relative to the repository.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns><see langword="true" /> if it exists and holds that.</returns>
    Task<bool> Holds(string folder, string commit, string path, CancellationToken cancellation);

    /// <summary>
    /// Takes the tree of the tracked and staged files as they are, without touching the index; it works with conflicts too. Files git does not track are left out, since an abort leaves them where they are.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <returns>The tree, or <see langword="null" /> when git cannot make it.</returns>
    Task<string?> Files(string folder);

    /// <summary>
    /// Puts a tree of files on top of the stash, where the user finds it, without changing any file.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="tree">The tree from <see cref="Files" />.</param>
    /// <param name="message">What the stash entry is.</param>
    /// <returns><see langword="true" /> if it was stored.</returns>
    Task<bool> Store(string folder, string tree, string message);
}

/// <inheritdoc />
internal sealed class GitSafeguards(IGitService git) : IGitSafeguards
{
    private readonly int kept = 100;

    private long sequence;

    /// <inheritdoc />
    public async Task<string?> Operation(string folder, CancellationToken cancellation)
    {
        foreach ((string path, string operation) in new[] { ("rebase-merge", "rebase"), ("rebase-apply", "rebase"), ("MERGE_HEAD", "merge"), ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert") })
        {
            GitOutput found = await git.Run(folder, ["rev-parse", "--git-path", path], cancellation);

            if (found.ExitCode == 0 && found.Output.Trim() is { Length: > 0 } relative && Path.Exists(Path.Combine(folder, relative)))
            {
                return operation;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> InTheWay(string folder, string tree, string? range, CancellationToken cancellation)
    {
        // Without excluding anything this lists ignored files too.
        string[] untracked = Paths(await Output(folder, ["ls-files", "--others", "-z"], cancellation));

        if (untracked.Length == 0)
        {
            return [];
        }

        HashSet<string> written = [.. Paths(await Output(folder, ["ls-tree", "-r", "--name-only", "-z", tree, "--"], cancellation))];
        HashSet<string> replayed = range is null ? [] : [.. Paths(await Output(folder, ["log", "--no-renames", "--name-only", "--format=", "-z", range, "--"], cancellation)).Select(path => path.TrimStart('\n')).Where(path => path.Length > 0)];
        HashSet<string> files = [.. written, .. replayed];
        HashSet<string> folders = [.. files.SelectMany(Parents)];
        List<string> found = [];

        foreach (string entry in untracked)
        {
            string path = entry.TrimEnd('/');

            if (folders.Contains(path) || Parents(path).Any(files.Contains) || replayed.Contains(path) || written.Contains(path) && !await Holds(folder, tree, path, cancellation))
            {
                found.Add(path);
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task Keep(string folder, string name, string commit)
    {
        if (commit.Length == 0)
        {
            return;
        }

        string root = "refs/auto-remote/backup";
        await git.Run(folder, ["update-ref", $"{root}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Interlocked.Increment(ref sequence)}/{name}", commit], CancellationToken.None);
        GitOutput listed = await git.Run(folder, ["for-each-ref", "--sort=-refname", "--format=%(refname)", root], CancellationToken.None);

        foreach (string old in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(kept))
        {
            await git.Run(folder, ["update-ref", "-d", old.Trim()], CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task<bool> Holds(string folder, string commit, string path, CancellationToken cancellation)
    {
        FileInfo file = new(Path.Combine(folder, path));
        GitOutput there = await git.Run(folder, ["rev-parse", "--verify", "--quiet", $"{commit}:{path}"], cancellation);

        if (there.ExitCode != 0)
        {
            return false;
        }

        // Hashing a link would hash what it points at.
        if (file.LinkTarget is { } target)
        {
            GitOutput stored = await git.Run(folder, ["cat-file", "blob", there.Output.Trim()], cancellation);
            return stored.ExitCode == 0 && stored.Output == target;
        }

        if (!file.Exists)
        {
            return false;
        }

        GitOutput here = await git.Run(folder, ["hash-object", "--", path], cancellation);
        return here.ExitCode == 0 && there.Output.Trim() == here.Output.Trim();
    }

    /// <inheritdoc />
    public async Task<string?> Files(string folder)
    {
        GitOutput index = await git.Run(folder, ["rev-parse", "--path-format=absolute", "--git-path", "index"], CancellationToken.None);
        string copy = Path.Combine(Path.GetTempPath(), "autoremote-index-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (File.Exists(index.Output.Trim()))
            {
                File.Copy(index.Output.Trim(), copy);
            }

            Dictionary<string, string> environment = new() { ["GIT_INDEX_FILE"] = copy };

            if ((await git.Run(folder, ["add", "-u"], environment, CancellationToken.None)).ExitCode != 0)
            {
                return null;
            }

            GitOutput written = await git.Run(folder, ["write-tree"], environment, CancellationToken.None);
            return written.ExitCode == 0 && written.Output.Trim() is { Length: > 0 } made ? made : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <inheritdoc />
    public async Task<bool> Store(string folder, string tree, string message)
    {
        // A stash entry is a commit of the files whose second parent is a commit of what was staged.
        GitOutput staged = await git.Run(folder, ["commit-tree", tree, "-p", "HEAD", "-m", message], CancellationToken.None);

        if (staged.ExitCode != 0)
        {
            return false;
        }

        GitOutput entry = await git.Run(folder, ["commit-tree", tree, "-p", "HEAD", "-p", staged.Output.Trim(), "-m", message], CancellationToken.None);
        return entry.ExitCode == 0 && (await git.Run(folder, ["stash", "store", "-m", message, entry.Output.Trim()], CancellationToken.None)).ExitCode == 0;
    }

    private async Task<string> Output(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, arguments, cancellation);
        return result.ExitCode == 0 ? result.Output : throw new RequestException($"Git could not check for files in the way: {result.Error.Trim()}");
    }

    private string[] Paths(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private IEnumerable<string> Parents(string path)
    {
        for (int at = path.LastIndexOf('/'); at > 0; at = path.LastIndexOf('/', at - 1))
        {
            yield return path[..at];
        }
    }
}
