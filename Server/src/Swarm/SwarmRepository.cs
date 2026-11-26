namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The git work of the swarm. On the delegating server it snapshots a workspace without touching its branches, files or index, packs the snapshot, and turns results into branches whose changes can be reviewed and applied. On a worker it keeps a bare cache repository per sender and project, and writes the files of a task into a folder that is deliberately not a repository: nothing the AI writes there can configure the git commands the server runs afterwards.
/// </summary>
internal interface ISwarmRepository
{
    /// <summary>
    /// Commits the files of a workspace, uncommitted and untracked ones too but never ignored ones, on top of its current commit without moving anything, and keeps the commit under a hidden reference.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="taskId">The task the snapshot is for.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The snapshot commit, which is the current commit when nothing is uncommitted, and the key of the repository.</returns>
    /// <exception cref="RequestException">The folder is not the root of a git repository with a commit.</exception>
    Task<(string Commit, string RepoKey)> Snapshot(string folder, Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Keeps a commit as the base of a task under its hidden reference.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the reference is set.</returns>
    Task Pin(string folder, Guid taskId, string commit, CancellationToken cancellation);

    /// <summary>
    /// Packs the base of a task into a git bundle, leaving out the commits the worker already has.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="taskId">The task, whose base is pinned.</param>
    /// <param name="known">Commits the worker has.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The bundle, empty when the worker already has everything.</returns>
    Task<ReadOnlyMemory<byte>> Bundle(string folder, Guid taskId, IReadOnlyList<string> known, CancellationToken cancellation);

    /// <summary>
    /// Merges the changes of a finished task into a base for a task that builds on it.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="current">The commit to merge into.</param>
    /// <param name="dependencyBase">The commit the finished task started from.</param>
    /// <param name="dependencyResult">The commit holding its changes.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The merged commit.</returns>
    /// <exception cref="RequestException">The changes conflict.</exception>
    Task<string> Merge(string folder, string current, string dependencyBase, string dependencyResult, CancellationToken cancellation);

    /// <summary>
    /// Gets the address of the project's remote, <c>origin</c>, without any password in it.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The address.</returns>
    /// <exception cref="RequestException">The workspace has no <c>origin</c> remote.</exception>
    Task<string> Remote(string folder, CancellationToken cancellation);

    /// <summary>
    /// Pushes a commit to a branch of the project's remote.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="remote">The remote's name or address.</param>
    /// <param name="commit">The commit or reference.</param>
    /// <param name="branch">The branch of the remote.</param>
    /// <param name="cancellation">Cancels the push.</param>
    /// <returns>A task that completes once the push is done.</returns>
    /// <exception cref="RequestException">The remote refused or cannot be reached.</exception>
    Task Push(string folder, string remote, string commit, string branch, CancellationToken cancellation);

    /// <summary>
    /// Deletes branches of the project's remote; a branch that cannot be deleted is left.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="branches">The branches.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the attempt is done.</returns>
    Task DeleteRemote(string folder, IReadOnlyList<string> branches, CancellationToken cancellation);

    /// <summary>
    /// Takes the result of a task from a bundle into a new branch of the workspace, checking that it is one commit on the task's base.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="bundle">The bundle.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The task's base.</param>
    /// <param name="branch">The wanted branch name.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The result commit and the branch made for it.</returns>
    /// <exception cref="RequestException">The bundle does not hold such a result.</exception>
    Task<(string Commit, string Branch)> Import(string folder, ReadOnlyMemory<byte> bundle, Guid taskId, string baseCommit, string branch, CancellationToken cancellation);

    /// <summary>
    /// Takes the result of a task from a branch of the project's remote into a new branch of the workspace, checking it as <see cref="Import" /> does.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="remoteBranch">The branch of the remote.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The task's base.</param>
    /// <param name="branch">The wanted branch name.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The result commit and the branch made for it.</returns>
    /// <exception cref="RequestException">The branch cannot be fetched or does not hold such a result.</exception>
    Task<(string Commit, string Branch)> Fetch(string folder, string remoteBranch, Guid taskId, string baseCommit, string branch, CancellationToken cancellation);

    /// <summary>
    /// Describes the changes between two commits: a summary per file and the patch, of one file when a path is given, cut at a limit.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="baseCommit">The commit before.</param>
    /// <param name="resultCommit">The commit after.</param>
    /// <param name="path">The only file whose patch is wanted, or <see langword="null" />.</param>
    /// <param name="limit">The most characters of patch.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The description.</returns>
    Task<string> Changes(string folder, string baseCommit, string resultCommit, string? path, int limit, CancellationToken cancellation);

    /// <summary>
    /// Applies the changes between two commits to the files of a workspace, merging them with what changed meanwhile, without committing them and without touching the index.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="baseCommit">The commit before.</param>
    /// <param name="resultCommit">The commit after.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>What was applied, with any files left with conflicts.</returns>
    /// <exception cref="RequestException">Nothing could be applied.</exception>
    Task<SwarmApplyResult> Apply(string folder, string baseCommit, string resultCommit, CancellationToken cancellation);

    /// <summary>
    /// Deletes the hidden references of a task and its result branch, unless the branch is checked out.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="branch">The result branch, or <see langword="null" />.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once done.</returns>
    Task Release(string folder, Guid taskId, string? branch, CancellationToken cancellation);

    /// <summary>
    /// Takes the base of a task into a worker's cache repository, making the repository when needed.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="bundle">The bundle, which may be empty.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The base.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns><see langword="false" /> when the cache lacks commits the bundle builds on.</returns>
    /// <exception cref="RequestException">The bundle is damaged or does not hold the base.</exception>
    Task<bool> Receive(string cache, ReadOnlyMemory<byte> bundle, Guid taskId, string baseCommit, CancellationToken cancellation);

    /// <summary>
    /// Fetches the base of a task from the project's remote into a worker's cache repository.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="url">The remote's address, which must be a network address.</param>
    /// <param name="branch">The branch holding the base.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The base.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the base is there.</returns>
    /// <exception cref="RequestException">The address is not allowed, the remote cannot be reached or does not hold the base.</exception>
    Task FetchBase(string cache, string url, string branch, Guid taskId, string baseCommit, CancellationToken cancellation);

    /// <summary>
    /// Writes the files of a commit into a new folder, which does not become a repository; symbolic links become plain files holding their target.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="folder">The new folder.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once the files are written.</returns>
    Task Export(string cache, string folder, string commit, CancellationToken cancellation);

    /// <summary>
    /// Commits the files of a task's folder, ignored ones aside, as one commit on its base.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="folder">The task's folder.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The base.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The commit, or <see langword="null" /> when nothing changed.</returns>
    Task<string?> Collect(string cache, string folder, Guid taskId, string baseCommit, string message, CancellationToken cancellation);

    /// <summary>
    /// Packs the result commit of a task into a bundle that builds on its base.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="baseCommit">The base.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The bundle.</returns>
    Task<ReadOnlyMemory<byte>> BundleResult(string cache, Guid taskId, string baseCommit, CancellationToken cancellation);

    /// <summary>
    /// Pushes the result commit of a task to a branch of the project's remote.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="url">The remote's address.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="cancellation">Cancels the push.</param>
    /// <returns>A task that completes once the push is done.</returns>
    /// <exception cref="RequestException">The address is not allowed or the push failed.</exception>
    Task PushResult(string cache, string url, Guid taskId, string branch, CancellationToken cancellation);

    /// <summary>
    /// Removes what a worker's cache keeps for one task.
    /// </summary>
    /// <param name="cache">The cache repository.</param>
    /// <param name="taskId">The task.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once done.</returns>
    Task Forget(string cache, Guid taskId, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a commit is in a repository.
    /// </summary>
    /// <param name="folder">The repository.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    Task<bool> Has(string folder, string commit, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class SwarmRepository(IGitService git) : ISwarmRepository
{
    private readonly int maxBundle = 256 * 1024 * 1024;

    private readonly TimeSpan transferTimeout = TimeSpan.FromMinutes(30);

    private readonly Regex hash = new("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant);

    private readonly Regex scp = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*@[A-Za-z0-9][A-Za-z0-9.-]*:[^:\\]", RegexOptions.CultureInvariant);

    // What configures an AI tool in a project can start programs, so it never travels into a task or back from one.
    private readonly string[] toolDirectories = [".opencode", ".claude", ".codex"];

    private readonly string[] toolFiles = ["opencode.json", "opencode.jsonc", ".mcp.json"];

    private readonly Dictionary<string, string> identity = new() { ["GIT_AUTHOR_NAME"] = "Auto Remote", ["GIT_AUTHOR_EMAIL"] = "auto-remote@localhost", ["GIT_COMMITTER_NAME"] = "Auto Remote", ["GIT_COMMITTER_EMAIL"] = "auto-remote@localhost" };

    /// <summary>
    /// Gets a value indicating whether workers also fetch from local paths, which only tests turn on: a sender naming a local path could otherwise have a worker send it any repository of the worker.
    /// </summary>
    internal bool AllowLocalRemotes { get; init; }

    /// <inheritdoc />
    public async Task<(string Commit, string RepoKey)> Snapshot(string folder, Guid taskId, CancellationToken cancellation)
    {
        GitOutput top = await git.Run(folder, ["rev-parse", "--show-toplevel"], cancellation);

        if (top.ExitCode != 0 || !SameFolder(top.Output.Trim(), folder))
        {
            throw new RequestException("Delegating needs a workspace that is the root of a git repository.");
        }

        GitOutput current = await git.Run(folder, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellation);

        if (current.ExitCode != 0)
        {
            throw new RequestException("Delegating needs a git repository with at least one commit.");
        }

        string head = current.Output.Trim();
        string tree = await WorkingTree(folder, await IndexPath(folder, cancellation), null, cancellation);
        string commit = tree == await Text(folder, ["rev-parse", "HEAD^{tree}"], null, cancellation) ? head : await Text(folder, ["commit-tree", tree, "-p", head, "-m", "Uncommitted changes for delegation (Auto Remote)"], identity, cancellation);
        string key = (await Text(folder, ["rev-list", "--max-parents=0", "HEAD"], null, cancellation)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).First();
        await Pin(folder, taskId, commit, cancellation);
        return (commit, key);
    }

    /// <inheritdoc />
    public Task Pin(string folder, Guid taskId, string commit, CancellationToken cancellation) => Text(folder, ["update-ref", BaseReference(taskId), Commit(commit)], null, cancellation);

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> Bundle(string folder, Guid taskId, IReadOnlyList<string> known, CancellationToken cancellation)
    {
        List<string> excluded = [];

        foreach (string commit in known.Where(commit => hash.IsMatch(commit)))
        {
            if (await Has(folder, commit, cancellation))
            {
                excluded.Add(commit);
            }
        }

        return await Pack(folder, [BaseReference(taskId), .. excluded.Count > 0 ? (IEnumerable<string>)["--not", .. excluded] : []], cancellation);
    }

    /// <inheritdoc />
    public async Task<string> Merge(string folder, string current, string dependencyBase, string dependencyResult, CancellationToken cancellation)
    {
        GitOutput merged = await git.Run(folder, ["merge-tree", "--write-tree", $"--merge-base={Commit(dependencyBase)}", Commit(current), Commit(dependencyResult)], cancellation);

        if (merged.ExitCode != 0)
        {
            throw new RequestException(merged.ExitCode == 1 ? "The changes of the tasks it depends on conflict with each other." : Failure(merged));
        }

        string tree = merged.Output.Split('\n')[0].Trim();
        return await Text(folder, ["commit-tree", tree, "-p", current, "-p", dependencyResult, "-m", "Changes of the tasks this one depends on (Auto Remote)"], identity, cancellation);
    }

    /// <inheritdoc />
    public async Task<string> Remote(string folder, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, ["remote", "get-url", "origin"], cancellation);

        if (result.ExitCode != 0 || result.Output.Trim() is not { Length: > 0 } url)
        {
            throw new RequestException("This workspace has no origin remote, which the git remote mode needs.");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https" && address.UserInfo.Length > 0)
        {
            return new UriBuilder(address) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
        }

        return url;
    }

    /// <inheritdoc />
    public Task Push(string folder, string remote, string commit, string branch, CancellationToken cancellation) => Text(folder, ["push", "--force", remote, $"{commit}:refs/heads/{branch}"], null, cancellation, transferTimeout);

    /// <inheritdoc />
    public async Task DeleteRemote(string folder, IReadOnlyList<string> branches, CancellationToken cancellation)
    {
        foreach (string branch in branches)
        {
            await git.Run(folder, ["push", "origin", $":refs/heads/{branch}"], cancellation, transferTimeout);
        }
    }

    /// <inheritdoc />
    public async Task<(string Commit, string Branch)> Import(string folder, ReadOnlyMemory<byte> bundle, Guid taskId, string baseCommit, string branch, CancellationToken cancellation)
    {
        using TemporaryFile file = new(".bundle");
        await File.WriteAllBytesAsync(file.Path, bundle, cancellation);
        await Text(folder, ["bundle", "verify", file.Path], null, cancellation);
        await Text(folder, ["fetch", "--no-tags", "--no-write-fetch-head", file.Path, $"+refs/results/{taskId:N}:{ResultReference(taskId)}"], null, cancellation, transferTimeout);
        return await Adopt(folder, taskId, baseCommit, branch, cancellation);
    }

    /// <inheritdoc />
    public async Task<(string Commit, string Branch)> Fetch(string folder, string remoteBranch, Guid taskId, string baseCommit, string branch, CancellationToken cancellation)
    {
        await Text(folder, ["fetch", "--no-tags", "--no-write-fetch-head", "origin", $"+refs/heads/{remoteBranch}:{ResultReference(taskId)}"], null, cancellation, transferTimeout);
        return await Adopt(folder, taskId, baseCommit, branch, cancellation);
    }

    /// <inheritdoc />
    public async Task<string> Changes(string folder, string baseCommit, string resultCommit, string? path, int limit, CancellationToken cancellation)
    {
        Dictionary<string, string> literal = new() { ["GIT_LITERAL_PATHSPECS"] = "1" };
        string summary = await Text(folder, ["diff", "--stat", "--no-ext-diff", Commit(baseCommit), Commit(resultCommit)], literal, cancellation, null, true);
        string patch = await Text(folder, ["diff", "--no-ext-diff", "--no-textconv", baseCommit, resultCommit, "--", .. path is null ? (string[])[] : [path]], literal, cancellation, null, true);
        string shown = patch.Length > limit ? $"{patch[..limit]}\n... the patch goes on for {patch.Length - limit} more characters; ask for one file at a time." : patch;
        return $"{summary}\n\n{shown}".Trim();
    }

    /// <inheritdoc />
    public async Task<SwarmApplyResult> Apply(string folder, string baseCommit, string resultCommit, CancellationToken cancellation)
    {
        using TemporaryFile patch = new(".patch");
        await Text(folder, ["diff", "--binary", "--full-index", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", $"--output={patch.Path}", Commit(baseCommit), Commit(resultCommit)], null, cancellation);
        int files = (await Text(folder, ["diff", "--name-only", "-z", baseCommit, resultCommit], null, cancellation, null, true)).Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;

        if (files == 0)
        {
            return new SwarmApplyResult([], 0);
        }

        using TemporaryFile index = new(".index");
        CopyIndex(await IndexPath(folder, cancellation), index.Path);
        Dictionary<string, string> environment = new() { ["GIT_INDEX_FILE"] = index.Path };
        await Text(folder, ["add", "-A"], environment, cancellation);
        GitOutput applied = await git.Run(folder, ["apply", "--3way", "--whitespace=nowarn", patch.Path], environment, cancellation);

        if (applied.ExitCode == 0)
        {
            return new SwarmApplyResult([], files);
        }

        string[] conflicts = (await Text(folder, ["diff", "--name-only", "-z", "--diff-filter=U"], environment, cancellation, null, true)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return conflicts.Length > 0 ? new SwarmApplyResult(conflicts, files) : throw new RequestException($"The changes could not be applied: {Failure(applied)}");
    }

    /// <inheritdoc />
    public async Task Release(string folder, Guid taskId, string? branch, CancellationToken cancellation)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        await git.Run(folder, ["update-ref", "-d", BaseReference(taskId)], cancellation);
        await git.Run(folder, ["update-ref", "-d", ResultReference(taskId)], cancellation);

        if (branch is not null && branch.StartsWith("swarm/", StringComparison.Ordinal))
        {
            await git.Run(folder, ["branch", "-D", branch], cancellation);
        }
    }

    /// <inheritdoc />
    public async Task<bool> Receive(string cache, ReadOnlyMemory<byte> bundle, Guid taskId, string baseCommit, CancellationToken cancellation)
    {
        await Prepare(cache, cancellation);

        if (!bundle.IsEmpty)
        {
            using TemporaryFile file = new(".bundle");
            await File.WriteAllBytesAsync(file.Path, bundle, cancellation);
            GitOutput verified = await git.Run(cache, ["bundle", "verify", file.Path], cancellation);

            if (verified.ExitCode != 0)
            {
                return verified.Error.Contains("prerequisite", StringComparison.OrdinalIgnoreCase) ? false : throw new RequestException($"The bundle is not usable: {Failure(verified)}");
            }

            await Text(cache, ["fetch", "--no-tags", "--no-write-fetch-head", file.Path, $"+{BaseReference(taskId)}:{WorkerReference(taskId)}"], null, cancellation, transferTimeout);
        }
        else if (!await Has(cache, Commit(baseCommit), cancellation))
        {
            return false;
        }
        else
        {
            await Text(cache, ["update-ref", WorkerReference(taskId), baseCommit], null, cancellation);
        }

        await Expect(cache, WorkerReference(taskId), baseCommit, cancellation);
        return true;
    }

    /// <inheritdoc />
    public async Task FetchBase(string cache, string url, string branch, Guid taskId, string baseCommit, CancellationToken cancellation)
    {
        RequireAllowed(url);
        await Prepare(cache, cancellation);
        await Text(cache, ["fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", url, $"+refs/heads/{branch}:{WorkerReference(taskId)}"], null, cancellation, transferTimeout);
        await Expect(cache, WorkerReference(taskId), baseCommit, cancellation);
    }

    /// <inheritdoc />
    public async Task Export(string cache, string folder, string commit, CancellationToken cancellation)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new RequestException($"The folder '{folder}' is not empty.");
        }

        Directory.CreateDirectory(folder);
        using TemporaryFile index = new(".index");
        Dictionary<string, string> environment = Detached(cache, folder, index.Path);
        await Text(folder, ["read-tree", Commit(commit)], environment, cancellation);
        await Text(folder, ["checkout-index", "--all", "--force"], environment, cancellation, transferTimeout);
        RemoveNamed(folder, name => IsToolConfiguration(name));
    }

    /// <inheritdoc />
    public async Task<string?> Collect(string cache, string folder, Guid taskId, string baseCommit, string message, CancellationToken cancellation)
    {
        if (!Directory.Exists(folder))
        {
            throw new RequestException("The folder of the task is gone.");
        }

        // git looks into a nested repository while adding files and runs whatever its configuration says, so none may be left.
        RemoveNamed(folder, name => string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase));
        using TemporaryFile index = new(".index");
        Dictionary<string, string> environment = Detached(cache, folder, index.Path);
        await Text(folder, ["read-tree", Commit(baseCommit)], environment, cancellation);
        List<(string Entry, string Path)> submodules = [.. (await Text(folder, ["ls-files", "--stage", "-z"], environment, cancellation, null, true)).Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("160000 ", StringComparison.Ordinal)).Select(line => (line[7..line.IndexOf(' ', 7)], line[(line.IndexOf('\t') + 1)..]))];

        // Submodules stay as the base has them: git is kept from looking inside them and their entries are put back unchanged.
        foreach ((_, string path) in submodules)
        {
            await Text(folder, ["update-index", "--force-remove", "--", path], environment, cancellation);
        }

        List<string> excluded = [.. toolDirectories.Select(name => $":(exclude,glob,icase)**/{name}/**"), .. toolDirectories.Concat(toolFiles).Select(name => $":(exclude,glob,icase)**/{name}"), .. submodules.Select(submodule => $":(exclude,literal){submodule.Path}")];
        await Text(folder, ["-c", "core.fsmonitor=false", "-c", $"core.hooksPath={Path.Combine(cache, "no-hooks")}", "add", "-A", "--", ".", .. excluded], environment, cancellation, transferTimeout);

        foreach ((string entry, string path) in submodules)
        {
            await Text(folder, ["update-index", "--add", "--cacheinfo", $"160000,{entry},{path}"], environment, cancellation);
        }

        string tree = await Text(folder, ["write-tree"], environment, cancellation);

        if (tree == await Text(cache, ["rev-parse", $"{baseCommit}^{{tree}}"], null, cancellation))
        {
            return null;
        }

        string commit = await Text(cache, ["commit-tree", tree, "-p", baseCommit, "-m", message], identity, cancellation);
        await Text(cache, ["update-ref", $"refs/results/{taskId:N}", commit], null, cancellation);
        return commit;
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> BundleResult(string cache, Guid taskId, string baseCommit, CancellationToken cancellation) => Pack(cache, [$"refs/results/{taskId:N}", $"^{Commit(baseCommit)}"], cancellation);

    /// <inheritdoc />
    public Task PushResult(string cache, string url, Guid taskId, string branch, CancellationToken cancellation)
    {
        RequireAllowed(url);
        return Push(cache, url, $"refs/results/{taskId:N}", branch, cancellation);
    }

    /// <inheritdoc />
    public async Task Forget(string cache, Guid taskId, CancellationToken cancellation)
    {
        if (!Directory.Exists(cache))
        {
            return;
        }

        await git.Run(cache, ["update-ref", "-d", WorkerReference(taskId)], cancellation);
        await git.Run(cache, ["update-ref", "-d", $"refs/results/{taskId:N}"], cancellation);
    }

    /// <inheritdoc />
    public async Task<bool> Has(string folder, string commit, CancellationToken cancellation) => hash.IsMatch(commit) && (await git.Run(folder, ["cat-file", "-e", $"{commit}^{{commit}}"], cancellation)).ExitCode == 0;

    private async Task Prepare(string cache, CancellationToken cancellation)
    {
        if (File.Exists(Path.Combine(cache, "HEAD")))
        {
            return;
        }

        Directory.CreateDirectory(cache);
        await Text(cache, ["init", "--bare", "--quiet"], null, cancellation);
        await Text(cache, ["config", "core.symlinks", "false"], null, cancellation);
        await Text(cache, ["config", "core.autocrlf", "false"], null, cancellation);
    }

    private async Task<(string Commit, string Branch)> Adopt(string folder, Guid taskId, string baseCommit, string branch, CancellationToken cancellation)
    {
        string commit = await Text(folder, ["rev-parse", "--verify", $"{ResultReference(taskId)}^{{commit}}"], null, cancellation);
        string parents = await Text(folder, ["rev-list", "--parents", "-n", "1", commit], null, cancellation);

        if (!parents.Split(' ').Skip(1).SequenceEqual([Commit(baseCommit)]))
        {
            throw new RequestException("The result does not build on the code of the task, so it was not taken.");
        }

        string[] changed = (await Text(folder, ["diff", "--name-only", "--no-renames", "-z", Commit(baseCommit), commit], null, cancellation, null, true)).Split('\0', StringSplitOptions.RemoveEmptyEntries);

        if (changed.FirstOrDefault(path => path.Split('/').Any(IsToolConfiguration)) is { } configuration)
        {
            throw new RequestException($"The result changes the configuration of an AI tool ({configuration}), which could run commands on this machine, so it was not taken.");
        }

        string name = branch;

        for (int suffix = 2; (await git.Run(folder, ["rev-parse", "--verify", "--quiet", $"refs/heads/{name}"], cancellation)).ExitCode == 0; suffix++)
        {
            name = $"{branch}-{suffix}";
        }

        await Text(folder, ["update-ref", $"refs/heads/{name}", commit], null, cancellation);
        return (commit, name);
    }

    private async Task Expect(string cache, string reference, string commit, CancellationToken cancellation)
    {
        if (await Text(cache, ["rev-parse", "--verify", $"{reference}^{{commit}}"], null, cancellation) != commit)
        {
            throw new RequestException("The code that arrived is not the code of the task.");
        }
    }

    private async Task<ReadOnlyMemory<byte>> Pack(string folder, IReadOnlyList<string> revisions, CancellationToken cancellation)
    {
        using TemporaryFile file = new(".bundle");
        GitOutput made = await git.Run(folder, ["bundle", "create", file.Path, .. revisions], cancellation, transferTimeout);

        if (made.ExitCode != 0)
        {
            return made.Error.Contains("empty bundle", StringComparison.OrdinalIgnoreCase) ? ReadOnlyMemory<byte>.Empty : throw new RequestException(Failure(made));
        }

        if (new FileInfo(file.Path).Length > maxBundle)
        {
            throw new RequestException($"The code is larger than the {maxBundle / 1024 / 1024} MB a package may hold.");
        }

        return await File.ReadAllBytesAsync(file.Path, cancellation);
    }

    private void RequireAllowed(string url)
    {
        bool network = url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("ssh://", StringComparison.Ordinal) || url.StartsWith("git://", StringComparison.Ordinal) || scp.IsMatch(url);

        if (url.StartsWith('-') || url.Contains("::", StringComparison.Ordinal) || !network && !AllowLocalRemotes)
        {
            throw new RequestException("The remote of the project is not a network address, so this worker does not fetch from it.");
        }
    }

    // Only the server's own cache configures git here: no configuration of the user is read, so nothing the task's files name can start a program the user configured.
    private Dictionary<string, string> Detached(string cache, string folder, string index) => new() { ["GIT_DIR"] = Path.GetFullPath(cache), ["GIT_WORK_TREE"] = Path.GetFullPath(folder), ["GIT_INDEX_FILE"] = index, ["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetFullPath(cache), "no-global-config"), ["GIT_CONFIG_NOSYSTEM"] = "1" };

    private bool IsToolConfiguration(string name) => toolDirectories.Concat(toolFiles).Contains(name, StringComparer.OrdinalIgnoreCase);

    // Symbolic links are removed when they match but never followed.
    private void RemoveNamed(string folder, Func<string, bool> matches)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            bool directory = entry is DirectoryInfo && entry.LinkTarget is null;

            if (matches(entry.Name))
            {
                if (directory)
                {
                    ((DirectoryInfo)entry).Delete(true);
                }
                else
                {
                    entry.Delete();
                }
            }
            else if (directory)
            {
                RemoveNamed(entry.FullName, matches);
            }
        }
    }

    private async Task<string> IndexPath(string folder, CancellationToken cancellation) => await Text(folder, ["rev-parse", "--path-format=absolute", "--git-path", "index"], null, cancellation);

    private void CopyIndex(string index, string copy)
    {
        if (File.Exists(index))
        {
            File.Copy(index, copy, true);
        }
    }

    private async Task<string> WorkingTree(string folder, string index, Dictionary<string, string>? extra, CancellationToken cancellation)
    {
        using TemporaryFile copy = new(".index");
        CopyIndex(index, copy.Path);
        Dictionary<string, string> environment = new(extra ?? []) { ["GIT_INDEX_FILE"] = copy.Path };
        await Text(folder, ["add", "-A"], environment, cancellation, transferTimeout);
        return await Text(folder, ["write-tree"], environment, cancellation);
    }

    private async Task<string> Text(string folder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellation, TimeSpan? timeout = null, bool raw = false)
    {
        GitOutput result = await git.Run(folder, arguments, environment ?? new Dictionary<string, string>(), cancellation, timeout);

        if (result.ExitCode != 0)
        {
            throw new RequestException(Failure(result)) { IsTemporary = true };
        }

        return raw ? result.Output : result.Output.Trim();
    }

    private string Failure(GitOutput result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim() is { Length: > 0 } text ? text : "Git failed.";

    private string Commit(string commit) => hash.IsMatch(commit) ? commit : throw new RequestException("A commit of the package is not valid.");

    private bool SameFolder(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string BaseReference(Guid taskId) => $"refs/auto-remote/tasks/{taskId:N}";

    private string ResultReference(Guid taskId) => $"refs/auto-remote/results/{taskId:N}";

    private string WorkerReference(Guid taskId) => $"refs/tasks/{taskId:N}";

    private sealed class TemporaryFile(string extension) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"auto-remote-swarm-{Guid.NewGuid():N}{extension}");

        public void Dispose() => File.Delete(Path);
    }
}
