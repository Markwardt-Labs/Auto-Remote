namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The git workflows of a workspace that go beyond one command: they take turns, can be cancelled and undone, push after rewriting history and have the AI resolve merge conflicts.
/// </summary>
internal interface IWorkspaceVersioning
{
    /// <summary>
    /// Checks whether a git action the user should see is running in a workspace; the background fetch and pull do not count.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns><see langword="true" /> if one is running.</returns>
    bool IsBusy(Guid id);

    /// <summary>
    /// Cancels the git action running in a workspace, which undoes what it had done; does nothing when none runs.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the action has ended and been undone.</returns>
    Task Cancel(Guid id);

    /// <summary>
    /// Runs one git action in a workspace, alone: it refuses while another runs, tells every client when it starts and ends, and when it is cancelled or fails unexpectedly puts the repository back as it was.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="revert">Whether a cancelled or crashed action is undone.</param>
    /// <param name="action">The action.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <param name="background">Whether the action is the automatic fetch or pull: it is not reported as busy and tells clients only when it changed something.</param>
    /// <exception cref="RequestException">Another action runs, or the action was cancelled by the user or refused by git.</exception>
    Task Perform(WorkspaceInfo workspace, bool revert, Func<CancellationToken, Task> action, CancellationToken cancellation, bool background = false);

    /// <summary>
    /// Checks out a branch, tag or commit, creating a local branch for one that exists only on the remote.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="name">The branch, tag or commit.</param>
    /// <param name="discard">Whether to discard changes that are not committed first; without it they make the checkout fail.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The name is unknown, or changes are in the way.</exception>
    Task Checkout(WorkspaceInfo workspace, string name, bool discard, CancellationToken cancellation);

    /// <summary>
    /// Asks the AI for a commit message of eight words or less for the changes that are not committed yet.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <returns>The message.</returns>
    /// <exception cref="RequestException">There is nothing to commit, or the AI gave no message.</exception>
    Task<string> CommitMessage(WorkspaceInfo workspace, CancellationToken cancellation);

    /// <summary>
    /// Deletes a branch locally and on the remote.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="name">The branch.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The branch is checked out or unknown, or the remote refused.</exception>
    Task DeleteBranch(WorkspaceInfo workspace, string name, CancellationToken cancellation);

    /// <summary>
    /// Deletes a tag locally and on the remote.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="name">The tag.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The tag is unknown, or the remote refused.</exception>
    Task DeleteTag(WorkspaceInfo workspace, string name, CancellationToken cancellation);

    /// <summary>
    /// Compares a branch with another.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="baseBranch">The branch it is compared with.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>How they stand.</returns>
    /// <exception cref="RequestException">A branch is unknown.</exception>
    Task<GitBranchState> BranchState(WorkspaceInfo workspace, string branch, string baseBranch, CancellationToken cancellation);

    /// <summary>
    /// Collapses the commits a branch has that another lacks into one, then force-pushes the branch; the working tree is not touched.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="branch">The branch to squash.</param>
    /// <param name="baseBranch">The branch it is squashed against.</param>
    /// <param name="message">The message of the one commit.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The branch has nothing to squash, the squash failed, or the push failed.</exception>
    Task Squash(WorkspaceInfo workspace, string branch, string baseBranch, string message, CancellationToken cancellation);

    /// <summary>
    /// Rebases a branch onto another, squashing its own commits first when asked, has the AI resolve conflicts, then force-pushes the branch. It needs a clean working tree and leaves the original branch checked out.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="branch">The branch to rebase.</param>
    /// <param name="onto">The branch to rebase it onto.</param>
    /// <param name="squashMessage">The message of the one commit its own commits are squashed into, or <see langword="null" /> not to squash.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">There are changes, the branch is up to date, the conflicts could not be resolved, or the push failed.</exception>
    Task Rebase(WorkspaceInfo workspace, string branch, string onto, string? squashMessage, CancellationToken cancellation);

    /// <summary>
    /// Brings a branch into another as a fast-forward, rebasing it first when needed, pushes the target and deletes the merged branch everywhere; the target is left checked out. It needs a clean working tree.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="source">The branch to merge.</param>
    /// <param name="target">The branch it is merged into.</param>
    /// <param name="squashMessage">The message of the one commit the source is squashed into, or <see langword="null" /> not to squash.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">There are changes, nothing to merge, the conflicts could not be resolved, or the push failed.</exception>
    Task Merge(WorkspaceInfo workspace, string source, string target, string? squashMessage, CancellationToken cancellation);

    /// <summary>
    /// Fetches the remote with pruning and moves the local branches that have nothing of their own to match it, as a git action; does nothing without a remote.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the action.</param>
    Task Fetch(WorkspaceInfo workspace, CancellationToken cancellation);

    /// <summary>
    /// Fetches, then fast-forwards the checked out branch onto its remote counterpart, stashing and restoring changes that are not committed; the AI resolves conflicts. Does nothing without a remote, or unless the workspace is ready.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The pull or the restore of the changes failed.</exception>
    Task Sync(WorkspaceInfo workspace, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class WorkspaceVersioning(IGitService git, IPromptService prompts, IEventHub events, IWorkspaceActivity activity, IWorkQueue? queue = null, IGitSafeguards? guards = null) : IWorkspaceVersioning
{
    private readonly IGitSafeguards safeguards = guards ?? new GitSafeguards(git);

    private readonly TimeSpan changeTimeout = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, Running> running = new();

    private readonly int attempts = 3;

    private readonly int messageWords = 8;

    private readonly int aimWords = 6;

    private readonly int rejectWords = 12;

    private readonly int messageAttempts = 2;

    private readonly TimeSpan aiPoll = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public async Task<string> CommitMessage(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        string pending = await git.Pending(workspace.Folder, cancellation);
        string request = Request(pending);
        string? message = null;
        int count = 0;
        await using IAsyncDisposable? turn = queue is null ? null : await queue.Reserve(workspace.Id, cancellation);
        string latest = await git.Pending(workspace.Folder, cancellation);

        if (latest != pending)
        {
            request = Request(latest);
        }

        // Nothing else can end a question that hangs, and while it holds the local AI no other work starts.
        using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        patience.CancelAfter(MessagePatience);

        for (int attempt = 0; attempt < messageAttempts && (message is null || count > messageWords); attempt++)
        {
            string answer;

            try
            {
                answer = await prompts.Ask(workspace.Id, message is null ? request : $"{request}\n\nThis answer had {count} words, which is too long: {message}\nWrite another that is complete and has {aimWords} words or less.", patience.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new RequestException("The AI took too long to write a commit message.");
            }

            string line = answer.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            string[] words = line.Trim('"', '\'', '`', '.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 0)
            {
                message = string.Join(' ', words);
                count = words.Length;
            }
        }

        if (message is null)
        {
            throw new RequestException("The AI gave no commit message.");
        }

        return count >= rejectWords ? throw new RequestException($"The AI wrote a commit message of {count} words, which is too long.") : message;
    }

    /// <summary>
    /// Gets how long the AI has for a commit message once it is free to write it.
    /// </summary>
    internal TimeSpan MessagePatience { get; init; } = TimeSpan.FromMinutes(20);

    /// <inheritdoc />
    public bool IsBusy(Guid id) => running.TryGetValue(id, out Running? current) && !current.Background;

    /// <inheritdoc />
    public Task Cancel(Guid id)
    {
        if (!running.TryGetValue(id, out Running? current))
        {
            return Task.CompletedTask;
        }

        try
        {
            current.Source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return current.Done.Task;
    }

    /// <inheritdoc />
    public Task Perform(WorkspaceInfo workspace, bool revert, Func<CancellationToken, Task> action, CancellationToken cancellation, bool background = false) => Run(workspace, revert, true, (_, token) => action(token), cancellation, background);

    // An undone action puts back only the branches it noted as written; a single command writes the checked out one.
    private async Task Run(WorkspaceInfo workspace, bool revert, bool single, Func<Journal, CancellationToken, Task> action, CancellationToken cancellation, bool background = false)
    {
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Running record = new(source, background, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        if (!running.TryAdd(workspace.Id, record))
        {
            throw new RequestException("A git action is already running in this workspace.");
        }

        if (!background && !activity.TryEnter(workspace.Id, WorkspaceState.Managing))
        {
            running.TryRemove(workspace.Id, out _);
            throw new RequestException(activity.Describe(workspace.Id));
        }

        string before = background ? await Fingerprint(workspace.Folder) : string.Empty;

        if (!background)
        {
            Publish(workspace.Id);
        }

        try
        {
            Snapshot? snapshot = revert ? await Capture(workspace.Folder, CancellationToken.None) : null;
            Journal journal = new();

            if (single && snapshot?.Branch is { } checkedOut)
            {
                journal.Written.Add(checkedOut);
            }

            try
            {
                await action(journal, source.Token);
            }
            catch (OperationCanceledException)
            {
                bool undone = await Undo(workspace.Folder, snapshot, journal);
                cancellation.ThrowIfCancellationRequested();
                throw new RequestException(journal.Published ? "The git action was cancelled; what it had already pushed stays."
                    : undone ? "The git action was cancelled and undone."
                    : "The git action was cancelled but could not be undone completely. Check the repository before going on.");
            }
            catch (Exception exception) when (exception is not RequestException)
            {
                await Undo(workspace.Folder, snapshot, journal);
                throw;
            }
        }
        finally
        {
            running.TryRemove(workspace.Id, out _);

            try
            {
                if (!background)
                {
                    activity.Leave(workspace.Id, WorkspaceState.Managing);

                    if (prompts.IsBusy(workspace.Id))
                    {
                        activity.TryEnter(workspace.Id, WorkspaceState.Working);
                    }
                }

                if (!background || await Fingerprint(workspace.Folder) != before)
                {
                    Publish(workspace.Id);
                }
            }
            finally
            {
                record.Done.TrySetResult();
            }
        }
    }

    /// <inheritdoc />
    public Task Checkout(WorkspaceInfo workspace, string name, bool discard, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (_, token) =>
        {
            string folder = workspace.Folder;
            await RequireNoOperation(folder, token);
            string[] arguments = await CheckoutArguments(folder, name, token);
            string? aside = null;
            bool reset = false;

            if (await Pending(folder, token))
            {
                if (!discard)
                {
                    throw new RequestException("Checking out would discard your changes that are not committed.");
                }

                // Discarding writes what is committed back, over any file git does not track where one of those was, and a stash does not hold the ignored ones.
                await RequireNothingInTheWay(folder, "HEAD", token);

                if ((await Conflicted(folder, token)).Count > 0)
                {
                    // After the reset the changes are only in the stash, so what would refuse the checkout is looked for first.
                    await RequireNothingInTheWay(folder, arguments[2] switch { "-b" => arguments[^1], "--detach" => name, _ => $"refs/heads/{name}" }, token);
                    await DiscardConflicts(folder);
                    reset = true;
                }
                else
                {
                    // The changes are only set aside until the checkout worked, so one that is refused discards nothing.
                    aside = await Stash(folder, "Auto Remote: changes being discarded by a checkout");
                }
            }

            Position before = await Where(folder, CancellationToken.None);
            GitOutput result = await Change(folder, arguments);

            // A hook that fails after the checkout leaves it done.
            bool done = result.ExitCode == 0 || await Where(folder, CancellationToken.None) != before;
            bool kept = false;

            if (aside is not null)
            {
                if (done || await Changed(folder, ["stash", "apply", "--index", aside]))
                {
                    await Drop(folder, aside);
                }
                else
                {
                    await Change(folder, ["stash", "apply", aside]);
                    kept = true;
                }
            }

            if (!done)
            {
                throw new RequestException(kept || reset ? $"{Failure(result)} Your changes are kept in the stash." : Failure(result));
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task DeleteBranch(WorkspaceInfo workspace, string name, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (journal, token) =>
        {
            string folder = workspace.Folder;
            RequireName(name);
            bool local = await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], token);
            bool remote = await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/remotes/origin/{name}"], token);

            if (!local && !remote)
            {
                throw new RequestException($"There is no branch named '{name}'.");
            }

            if (local)
            {
                await safeguards.Keep(folder, name, await Tip(folder, name, token));
                journal.Written.Add(name);
                Check(await git.Run(folder, ["branch", "-D", name], token));
                await Forget(folder, name);
            }

            if (await OnRemote(folder, $"refs/heads/{name}", token) && !await Ok(folder, ["push", "origin", $":refs/heads/{name}"], token))
            {
                throw new RequestException($"Deleted '{name}' locally, but deleting it on the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task DeleteTag(WorkspaceInfo workspace, string name, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (_, token) =>
        {
            string folder = workspace.Folder;
            RequireName(name);

            if (!await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/tags/{name}"], token))
            {
                throw new RequestException($"There is no tag named '{name}'.");
            }

            Check(await git.Run(folder, ["tag", "-d", name], token));

            if (await OnRemote(folder, $"refs/tags/{name}", token) && !await Ok(folder, ["push", "origin", $":refs/tags/{name}"], token))
            {
                throw new RequestException($"Deleted '{name}' locally, but deleting it on the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public async Task<GitBranchState> BranchState(WorkspaceInfo workspace, string branch, string baseBranch, CancellationToken cancellation)
    {
        string folder = workspace.Folder;
        await Local(folder, branch, cancellation);
        await Local(folder, baseBranch, cancellation);
        return new GitBranchState
        {
            Unique = await Count(folder, branch, baseBranch, cancellation),
            IsBased = await Contains(folder, branch, baseBranch, cancellation),
            Subject = await Text(folder, ["log", "-1", "--format=%s", $"refs/heads/{branch}"], cancellation),
        };
    }

    /// <inheritdoc />
    public Task Squash(WorkspaceInfo workspace, string branch, string baseBranch, string message, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (journal, token) =>
        {
            string folder = workspace.Folder;
            RequireMessage(message);
            await RequireNoOperation(folder, token);
            await Local(folder, branch, token);
            await Local(folder, baseBranch, token);

            if (await Count(folder, branch, baseBranch, token) == 0)
            {
                throw new RequestException($"'{branch}' has no commits that aren't already on '{baseBranch}'.");
            }

            string? published = await Published(folder, branch, token);
            await RequireIncluded(folder, branch, published, token);

            await safeguards.Keep(folder, branch, await Tip(folder, branch, token));
            journal.Written.Add(branch);

            if (!await SquashUnique(folder, branch, baseBranch, message, token))
            {
                throw new RequestException($"Squashing '{branch}' failed - it was left unchanged.");
            }

            await Remember(folder, branch, published);

            if (!await PushRewritten(folder, branch, published, token))
            {
                throw new RequestException($"Squashed '{branch}' locally, but force-pushing it to the remote failed.");
            }

            journal.Published = true;
            await Forget(folder, branch);
        },
        cancellation);

    /// <inheritdoc />
    public Task Rebase(WorkspaceInfo workspace, string branch, string onto, string? squashMessage, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (journal, token) =>
        {
            string folder = workspace.Folder;
            await RequireNoOperation(folder, token);
            await RequireClean(folder, token);
            await Local(folder, branch, token);
            await Local(folder, onto, token);
            int count = await Count(folder, branch, onto, token);

            if (count == 0 && await Contains(folder, branch, onto, token))
            {
                throw new RequestException($"'{branch}' is already up to date with '{onto}'.");
            }

            string? published = await Published(folder, branch, token);
            await RequireIncluded(folder, branch, published, token);
            Position original = await Where(folder, token);

            try
            {
                await RebaseBranch(workspace, journal, branch, onto, count > 1 ? squashMessage : null, token);
            }
            catch (RequestException)
            {
                await Return(folder, original);
                throw;
            }

            await Remember(folder, branch, published);
            bool pushed = await PushRewritten(folder, branch, published, token);

            if (pushed)
            {
                journal.Published = true;
                await Forget(folder, branch);
            }

            await Return(folder, original);

            if (!pushed)
            {
                throw new RequestException($"Rebased '{branch}' locally, but force-pushing it to the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task Merge(WorkspaceInfo workspace, string source, string target, string? squashMessage, CancellationToken cancellation) => Run(
        workspace,
        true,
        false,
        async (journal, token) =>
        {
            string folder = workspace.Folder;
            await RequireNoOperation(folder, token);
            await RequireClean(folder, token);
            await Local(folder, source, token);
            await Local(folder, target, token);
            int count = await Count(folder, source, target, token);

            if (count == 0)
            {
                throw new RequestException($"'{source}' has no commits that aren't already on '{target}'.");
            }

            Position original = await Where(folder, token);

            // The target is left checked out, which would leave commits made on a detached head with nothing that reaches them.
            if (original.Branch is null && (await Text(folder, ["for-each-ref", "--contains", original.Head, "--count=1", "refs/heads", "refs/tags", "refs/remotes"], token)).Length == 0)
            {
                throw new RequestException("The checked out commit is on no branch. Create a branch for it first.");
            }

            // The source is deleted on the remote at the end, so nothing may be there that is not merged.
            string? published = await Published(folder, source, token);
            await RequireIncluded(folder, source, published, token);
            bool needsRebase = !await Contains(folder, source, target, token);
            string? message = count > 1 ? squashMessage : null;

            if (needsRebase || message is not null)
            {
                try
                {
                    await RebaseBranch(workspace, journal, source, target, message, token);
                }
                catch (RequestException)
                {
                    await Return(folder, original);
                    throw;
                }

                await Remember(folder, source, published);
            }

            string merged = await Tip(folder, source, token);
            await safeguards.Keep(folder, source, merged);
            await safeguards.Keep(folder, target, await Tip(folder, target, token));
            journal.Written.Add(target);

            if (!await FastForward(folder, target, source, token))
            {
                await Return(folder, original);
                throw new RequestException($"Couldn't fast-forward '{target}' to '{source}'.");
            }

            if (!await Push(folder, target, token))
            {
                throw new RequestException($"Merged into '{target}' locally, but pushing it to the remote failed.");
            }

            journal.Published = await HasRemote(folder, token);

            // The source is only deleted while everything it holds is in the target.
            if (await Tip(folder, source, token) != merged || !await Contains(folder, target, source, token))
            {
                throw new RequestException($"Merged, but '{source}' changed meanwhile, so it was not deleted.");
            }

            journal.Written.Add(source);
            Check(await git.Run(folder, ["branch", "-D", source], token));

            if (published is not null && !await Ok(folder, ["push", "origin", $"--force-with-lease=refs/heads/{source}:{published}", $":refs/heads/{source}"], token))
            {
                throw new RequestException($"Merged, but deleting '{source}' on the remote failed.");
            }

            await Forget(folder, source);
        },
        cancellation);

    /// <inheritdoc />
    public async Task Fetch(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        if (await HasRemote(workspace.Folder, cancellation))
        {
            await Perform(workspace, false, token => Mirror(workspace, false, token), cancellation);
        }
    }

    /// <inheritdoc />
    public async Task Sync(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        if (activity.Get(workspace.Id) != WorkspaceState.Ready || running.ContainsKey(workspace.Id) || !await HasRemote(workspace.Folder, cancellation))
        {
            return;
        }

        await Perform(
            workspace,
            false,
            async token =>
            {
                await Mirror(workspace, true, token);
                await Pull(workspace, token);
            },
            cancellation,
            true);
    }

    // Git 2.48+ creates refs/remotes/origin/HEAD on the first fetch, which is not a real change.
    private async Task<string> Fingerprint(string folder) => string.Join('\n', (await Text(folder, ["for-each-ref", "--format=%(refname) %(objectname)"], CancellationToken.None)).Split('\n').Where(line => !line.StartsWith("refs/remotes/") || !line.Split(' ')[0].EndsWith("/HEAD"))) + await Text(folder, ["rev-parse", "HEAD"], CancellationToken.None) + await Text(folder, ["status", "--porcelain"], CancellationToken.None);

    private async Task Mirror(WorkspaceInfo workspace, bool background, CancellationToken cancellation)
    {
        string folder = workspace.Folder;

        if (await safeguards.Operation(folder, cancellation) is not null)
        {
            return;
        }

        string? current = await Current(folder, cancellation);
        string? tracked = current is null ? null : await Remote(folder, current, cancellation);
        Dictionary<string, string> inSync = [];

        foreach (string branch in await Branches(folder, cancellation))
        {
            if (branch != current && await Remote(folder, branch, cancellation) is { } prior && prior == await Tip(folder, branch, cancellation))
            {
                inSync[branch] = prior;
            }
        }

        if (!await Ok(folder, ["fetch", "origin", "--tags", "--prune"], cancellation))
        {
            return;
        }

        // A branch checked out anywhere, in this tree or another, is left where it is.
        HashSet<string> checkedOut = [.. Lines(await Text(folder, ["worktree", "list", "--porcelain"], cancellation)).Where(line => line.StartsWith("branch refs/heads/", StringComparison.Ordinal)).Select(line => line["branch refs/heads/".Length..])];

        foreach (string branch in await Branches(folder, cancellation))
        {
            if (checkedOut.Contains(branch) || await Remote(folder, branch, cancellation) is not { } remote)
            {
                continue;
            }

            // Read again after the fetch, which can be long enough for a commit to be made.
            string local = await Tip(folder, branch, cancellation);
            bool unchanged = inSync.TryGetValue(branch, out string? was) && was == local;

            bool behind = remote != local && await Ok(folder, ["merge-base", "--is-ancestor", local, remote], cancellation);

            if (behind || remote != local && unchanged)
            {
                // Only a move that leaves commits behind needs them kept; keeping every one would push real backups out.
                if (!behind)
                {
                    await safeguards.Keep(folder, branch, local);
                }

                await git.Run(folder, ["update-ref", $"refs/heads/{branch}", remote, local], cancellation);
            }
        }

        // The checked out branch goes with its remote counterpart only when that is what it followed and had everything it holds.
        if (current is null || tracked is null || await Remote(folder, current, cancellation) is not null || await Text(folder, ["config", "--get", $"branch.{current}.remote"], cancellation) is not ("" or "origin"))
        {
            return;
        }

        await Exclusively(workspace.Id, background, async () =>
        {
            CancellationToken none = CancellationToken.None;

            if (await Current(folder, none) == current && await Ok(folder, ["merge-base", "--is-ancestor", $"refs/heads/{current}", tracked], none))
            {
                await safeguards.Keep(folder, current, await Tip(folder, current, none));

                if (await Changed(folder, ["checkout", "--detach"]))
                {
                    await git.Run(folder, ["branch", "-D", current], none);
                }
            }
        });
    }

    // The automatic action is not in the Managing state, so it takes it while it changes what is checked out, which keeps the AI from starting meanwhile; it does nothing when the AI already works.
    private async Task Exclusively(Guid id, bool background, Func<Task> change)
    {
        if (!background)
        {
            await change();
            return;
        }

        if (!activity.TryEnter(id, WorkspaceState.Managing))
        {
            return;
        }

        try
        {
            if (!prompts.IsBusy(id))
            {
                await change();
            }
        }
        finally
        {
            activity.Leave(id, WorkspaceState.Managing);

            if (prompts.IsBusy(id))
            {
                activity.TryEnter(id, WorkspaceState.Working);
            }
        }
    }

    private async Task Pull(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        string folder = workspace.Folder;

        if (await safeguards.Operation(folder, cancellation) is not null || await Current(folder, cancellation) is not { } branch)
        {
            return;
        }

        string original = await Text(folder, ["rev-parse", "HEAD"], cancellation);
        string? remote = await Remote(folder, branch, cancellation);

        if (remote is null || remote == original || !await Ok(folder, ["merge-base", "--is-ancestor", original, remote], cancellation))
        {
            return;
        }

        await Exclusively(workspace.Id, true, async () =>
        {
            if (await Current(folder, cancellation) == branch && await Text(folder, ["rev-parse", "HEAD"], cancellation) == original && await CanStash(folder, cancellation))
            {
                await FastForwardPending(workspace, branch, original, cancellation);
            }
        });
    }

    // Stashing writes what is committed back, which replaces ignored files where a file and a folder changed places, and it forgets edits of files git was told not to watch.
    private async Task<bool> CanStash(string folder, CancellationToken cancellation)
    {
        if (!await Pending(folder, cancellation))
        {
            return true;
        }

        if ((await safeguards.InTheWay(folder, "HEAD", null, cancellation)).Count > 0)
        {
            return false;
        }

        foreach (string line in Paths(await Raw(folder, ["ls-files", "-v", "-z"], cancellation)))
        {
            // A lower case letter marks a file assumed unchanged, S one that is skipped in the work tree.
            if ((char.IsLower(line[0]) || line[0] == 'S') && File.Exists(Path.Combine(folder, line[2..])) && !await safeguards.Holds(folder, string.Empty, line[2..], cancellation))
            {
                return false;
            }
        }

        return true;
    }

    // Once changes are stashed nothing cancels the git commands, so they never stay behind in the stash unnoticed.
    private async Task FastForwardPending(WorkspaceInfo workspace, string branch, string original, CancellationToken cancellation)
    {
        string folder = workspace.Folder;
        CancellationToken none = CancellationToken.None;
        cancellation.ThrowIfCancellationRequested();
        string? stash = await Pending(folder, none) ? await Stash(folder, "Auto Remote: changes set aside for a pull") : null;
        bool pulled = await Changed(folder, ["merge", "--ff-only", "--no-overwrite-ignore", $"refs/remotes/origin/{branch}"]);

        if (stash is not null && await Changed(folder, ["stash", "apply", "--index", stash]))
        {
            await Drop(folder, stash);
            stash = null;
        }

        if (!pulled)
        {
            throw stash is null ? new RequestException($"Pulling '{branch}' failed.") : Kept(workspace.Id, $"Pulling '{branch}' failed. Your changes are kept in the stash.");
        }

        if (stash is null)
        {
            return;
        }

        // What was staged cannot be restored as it was, so from here on the stash stays as the one complete copy.
        Outcome applied = await Classify(folder, await Change(folder, ["stash", "apply", stash]), none);

        if (applied == Outcome.Failed)
        {
            throw Kept(workspace.Id, "Restoring your changes after the pull failed; they are kept in the stash.");
        }

        if (applied == Outcome.Conflicts)
        {
            try
            {
                applied = await Resolve(workspace, null, applied, _ => Task.FromResult(Outcome.Succeeded), files => StashInstruction(files, original), cancellation);
            }
            catch (OperationCanceledException)
            {
                throw Kept(workspace.Id, "Stopped while your changes were being restored after the pull: conflicts are left to resolve, and your changes are kept in the stash.");
            }

            if (applied == Outcome.Conflicts)
            {
                throw Kept(workspace.Id, "Restoring your changes after the pull left conflicts that need resolving by hand; your changes are kept in the stash.");
            }
        }

        // Git may also have left out files it did not track, which then are only in the stash.
        if (!await RestoredUntracked(folder, stash))
        {
            throw Kept(workspace.Id, "Some of your files that git does not track could not be restored after the pull; everything is kept in the stash.");
        }

        Kept(workspace.Id, "Your changes were restored after a pull, but not exactly as they were, so a copy of them is kept in the stash.");
    }

    // The clients say nothing about the automatic pull, so what it leaves in the stash is told as a failure of the workspace, which they show.
    private RequestException Kept(Guid id, string message)
    {
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Time = DateTimeOffset.UtcNow, Error = message });
        return new RequestException(message);
    }

    // Git answers success without stashing anything when only a submodule changed, which would leave an older stash on top.
    private async Task<string?> Stash(string folder, string message)
    {
        string[] top = ["rev-parse", "--verify", "--quiet", "refs/stash"];
        string before = await Text(folder, top, CancellationToken.None);
        GitOutput pushed = await Change(folder, ["stash", "push", "-u", "-m", message]);
        string after = await Text(folder, top, CancellationToken.None);

        if (after.Length > 0 && after != before)
        {
            return after;
        }

        return pushed.ExitCode == 0 ? null : throw new RequestException($"Your changes could not be set aside: {Failure(pushed)}");
    }

    // Conflicts cannot be stashed the usual way, so the files are stored as they are before they are reset.
    private async Task DiscardConflicts(string folder)
    {
        if (await safeguards.Files(folder) is not { } files || !await safeguards.Store(folder, files, "Auto Remote: files with conflicts discarded by a checkout"))
        {
            throw new RequestException("The files with conflicts could not be saved, so nothing was discarded.");
        }

        await Change(folder, ["reset", "--hard"]);
    }

    private async Task Drop(string folder, string stash)
    {
        int index = Lines(await Text(folder, ["stash", "list", "--format=%H"], CancellationToken.None)).ToList().IndexOf(stash);

        if (index >= 0)
        {
            await git.Run(folder, ["stash", "drop", $"stash@{{{index}}}"], CancellationToken.None);
        }
    }

    private async Task<bool> RestoredUntracked(string folder, string stash)
    {
        // The third parent of a stash holds the untracked files; there is none when there were none.
        if (!await Ok(folder, ["rev-parse", "--verify", "--quiet", $"{stash}^3"], CancellationToken.None))
        {
            return true;
        }

        foreach (string path in Paths(await Raw(folder, ["ls-tree", "-r", "-z", "--name-only", $"{stash}^3"], CancellationToken.None)))
        {
            if (!await safeguards.Holds(folder, $"{stash}^3", path, CancellationToken.None))
            {
                return false;
            }
        }

        return true;
    }

    private async Task RebaseBranch(WorkspaceInfo workspace, Journal journal, string branch, string onto, string? squashMessage, CancellationToken cancellation)
    {
        string folder = workspace.Folder;
        string before = await Tip(folder, branch, cancellation);
        await safeguards.Keep(folder, branch, before);
        journal.Written.Add(branch);
        Outcome outcome;
        bool dropped = false;
        bool meddled = false;
        int expected = 0;

        try
        {
            if (!await Contains(folder, branch, onto, cancellation))
            {
                if (!await CheckoutBranch(folder, branch, cancellation))
                {
                    throw new RequestException($"'{branch}' could not be checked out to rebase it.");
                }

                // A rebase replaces ignored files without a word.
                if (await safeguards.InTheWay(folder, $"refs/heads/{onto}", $"refs/heads/{onto}..refs/heads/{branch}", cancellation) is { Count: > 0 } lost)
                {
                    throw new RequestException($"Rebasing '{branch}' onto '{onto}' would overwrite files that git does not track: {string.Join(", ", lost)}. Move them away first.");
                }
            }

            if (squashMessage is not null && !await SquashUnique(folder, branch, onto, squashMessage, cancellation))
            {
                throw new RequestException($"Squashing '{branch}' failed.");
            }

            expected = await Number(folder, ["rev-list", "--count", "--no-merges", "--cherry-pick", "--right-only", $"refs/heads/{onto}...refs/heads/{branch}"], cancellation);
            outcome = Outcome.Succeeded;

            if (!await Contains(folder, branch, onto, cancellation))
            {
                outcome = await Classify(folder, await Change(folder, ["-c", "rebase.updateRefs=false", "-c", "rebase.autoStash=false", "rebase", $"refs/heads/{onto}"]), CancellationToken.None);
                await Stopped(folder, journal, outcome);
                (string Commit, string Head) stop = await StoppedAt(folder);
                cancellation.ThrowIfCancellationRequested();
                outcome = await Resolve(workspace, journal, outcome, async token =>
                {
                    if (await safeguards.Operation(folder, token) != "rebase" || await StoppedAt(folder) != stop)
                    {
                        meddled = true;
                    }
                    else if (await Ok(folder, ["diff", "--cached", "--quiet", "HEAD"], token))
                    {
                        // Nothing is left of the commit git stopped at, which continuing would drop without a word.
                        dropped = true;
                        return Outcome.Failed;
                    }

                    // The conflict may have waited for hours, long enough for a file git does not track to appear in the way.
                    if (await safeguards.InTheWay(folder, "HEAD", $"refs/heads/{onto}..{before}", token) is { Count: > 0 } appeared)
                    {
                        throw new RequestException($"Continuing the rebase of '{branch}' would overwrite files that git does not track: {string.Join(", ", appeared)}.");
                    }

                    Outcome next = await Classify(folder, await Change(folder, ["-c", "rebase.updateRefs=false", "rebase", "--continue"]), CancellationToken.None);
                    await Stopped(folder, journal, next);
                    stop = await StoppedAt(folder);
                    token.ThrowIfCancellationRequested();
                    return next;
                }, ConflictInstruction, cancellation);

                // The AI may have continued the rebase to its end itself.
                if (outcome == Outcome.Failed && !dropped && await safeguards.Operation(folder, cancellation) is null && await Current(folder, cancellation) == branch && await Contains(folder, branch, onto, cancellation))
                {
                    meddled = true;
                    outcome = Outcome.Succeeded;
                }
            }
        }
        catch (RequestException)
        {
            await Unwind(folder, journal, branch, before);
            throw;
        }

        if (!dropped && outcome != Outcome.Succeeded)
        {
            await Unwind(folder, journal, branch, before);
            throw new RequestException(outcome == Outcome.Conflicts
                ? $"Could not automatically resolve the conflicts rebasing '{branch}' onto '{onto}' - aborted."
                : $"Rebasing '{branch}' onto '{onto}' failed.");
        }

        // Where the AI moved the rebase along itself, only counting the commits shows whether one was lost.
        if (dropped || meddled && await Number(folder, ["rev-list", "--count", "--no-merges", $"refs/heads/{onto}..refs/heads/{branch}"], cancellation) < expected)
        {
            await Unwind(folder, journal, branch, before);
            throw new RequestException($"Resolving the conflicts dropped a commit of '{branch}', so nothing was changed.");
        }
    }

    // Which commit the rebase stopped at and on what, which changes when anyone but this server moves it along.
    private async Task<(string Commit, string Head)> StoppedAt(string folder) => (await Text(folder, ["rev-parse", "--verify", "--quiet", "REBASE_HEAD"], CancellationToken.None), await Text(folder, ["rev-parse", "HEAD"], CancellationToken.None));

    private async Task Unwind(string folder, Journal journal, string branch, string tip)
    {
        CancellationToken none = CancellationToken.None;

        if (!await Abort(folder, journal))
        {
            throw new RequestException($"Rebasing '{branch}' failed, and the rebase could not be aborted without losing files, so it is left in progress. Finish or abort it by hand.");
        }

        string now = await Tip(folder, branch, none);

        if (tip.Length == 0 || now == tip)
        {
            return;
        }

        // Squashing, or the AI, moved the branch before the rebase failed.
        await safeguards.Keep(folder, branch, now);

        if (await Current(folder, none) == branch)
        {
            await Move(folder, tip, false, none);
        }
        else
        {
            await git.Run(folder, ["update-ref", $"refs/heads/{branch}", tip, now], none);
        }
    }

    private string Request(string pending) => $"Write a git commit message of {messageWords} words or less for the pending changes below. Describe only what those changes themselves do to the files, as someone who sees nothing but the diff would: ignore the conversation, the tasks or requests that led to them and any reason that is not visible in the changes. It must be one complete phrase that stands on its own, so aim for about {aimWords} words, count them, and rephrase it shorter rather than letting it run on. Answer with the message only, on one line, without quotes or a full stop.\n\n{pending}";

    private async Task<Outcome> Resolve(WorkspaceInfo workspace, Journal? journal, Outcome outcome, Func<CancellationToken, Task<Outcome>> proceed, Func<IReadOnlyList<string>, string> instruction, CancellationToken cancellation)
    {
        string folder = workspace.Folder;
        await Stopped(folder, journal, outcome);

        for (int attempt = 0; outcome == Outcome.Conflicts && attempt < attempts; attempt++)
        {
            IReadOnlyList<string> files = await Conflicted(folder, cancellation);

            try
            {
                await using IAsyncDisposable? turn = queue is null ? null : await queue.Reserve(workspace.Id, cancellation);
                await prompts.Prompt(workspace.Id, instruction(files), cancellation);
                await Idle(workspace.Id, cancellation);
                // Stopping the action also stops the AI, which must not be prompted again then.
                cancellation.ThrowIfCancellationRequested();
            }
            catch (RequestException)
            {
                return outcome;
            }

            if ((await Conflicted(folder, cancellation)).Count > 0)
            {
                continue;
            }

            outcome = await proceed(cancellation);
            await Stopped(folder, journal, outcome);
        }

        return outcome;
    }

    // The files as git left them when it stopped, so that whatever differs later was done by someone and is saved before an abort.
    private async Task Stopped(string folder, Journal? journal, Outcome outcome)
    {
        if (journal is not null && outcome == Outcome.Conflicts)
        {
            journal.Baseline = await safeguards.Files(folder);
        }
    }

    private async Task Idle(Guid id, CancellationToken cancellation)
    {
        while (prompts.IsBusy(id))
        {
            await Task.Delay(aiPoll, cancellation);
        }
    }

    private string ConflictInstruction(IReadOnlyList<string> files) =>
        "This produced merge conflicts in: " + string.Join(", ", files) + ". Open each file, resolve the conflict by editing it to the correct final content and removing the conflict markers (<<<<<<<, =======, >>>>>>>), then stage the resolved files with `git add`. Reply once every conflict is resolved and staged.";

    private string StashInstruction(IReadOnlyList<string> files, string original) =>
        "New commits were just pulled into the current branch, and reapplying your stashed pending changes on top produced merge conflicts in: " + string.Join(", ", files) + $". The current branch was at commit {original} before this pull - everything reachable from HEAD since then is a newly-pulled commit, not part of the stashed changes. Open each file, resolve the conflict by editing it to the correct final content - respecting both the intent of the stashed (previously pending, uncommitted) changes and everything the newly-pulled commits changed - and removing the conflict markers (<<<<<<<, =======, >>>>>>>), then stage the resolved files with `git add`. Reply once every conflict is resolved and staged.";

    private async Task<Outcome> Classify(string folder, GitOutput result, CancellationToken cancellation) => result.ExitCode == 0 ? Outcome.Succeeded : (await Conflicted(folder, cancellation)).Count > 0 ? Outcome.Conflicts : Outcome.Failed;

    private async Task<IReadOnlyList<string>> Conflicted(string folder, CancellationToken cancellation) => Lines(await Text(folder, ["diff", "--name-only", "--diff-filter=U"], cancellation));

    private async Task<bool> SquashUnique(string folder, string branch, string baseBranch, string message, CancellationToken cancellation)
    {
        string mergeBase = await Text(folder, ["merge-base", $"refs/heads/{baseBranch}", $"refs/heads/{branch}"], cancellation);
        string tip = await Text(folder, ["rev-parse", $"refs/heads/{branch}"], cancellation);

        if (mergeBase.Length == 0 || tip.Length == 0 || tip == mergeBase)
        {
            return false;
        }

        string created = await Text(folder, ["commit-tree", $"{tip}^{{tree}}", "-p", mergeBase, "-m", message], cancellation);
        return created.Length > 0 && await Ok(folder, ["update-ref", $"refs/heads/{branch}", created, tip], cancellation);
    }

    private async Task<bool> FastForward(string folder, string target, string source, CancellationToken cancellation)
    {
        if (!await Contains(folder, source, target, cancellation))
        {
            return false;
        }

        Position original = await Where(folder, cancellation);

        if (!await CheckoutBranch(folder, target, cancellation))
        {
            return false;
        }

        bool merged = await Changed(folder, ["merge", "--ff-only", "--no-overwrite-ignore", $"refs/heads/{source}"]);
        cancellation.ThrowIfCancellationRequested();

        if (!merged)
        {
            await Return(folder, original);
        }

        return merged;
    }

    private async Task<bool> CheckoutBranch(string folder, string branch, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        if (await Current(folder, cancellation) != branch)
        {
            await Switch(folder, branch);
        }

        cancellation.ThrowIfCancellationRequested();
        return await Current(folder, cancellation) == branch;
    }

    // Judged by where the head ended up, since a hook that fails afterwards leaves the checkout done.
    private async Task<bool> Switch(string folder, string branch)
    {
        await Change(folder, ["checkout", "--no-overwrite-ignore", branch, "--"]);
        return await Current(folder, CancellationToken.None) == branch;
    }

    private async Task<Position> Where(string folder, CancellationToken cancellation) => new(await Current(folder, cancellation), await Text(folder, ["rev-parse", "HEAD"], cancellation));

    private async Task Return(string folder, Position position)
    {
        // Checking out in the middle of an operation that could not be aborted would have finishing it by hand move that branch.
        if (await safeguards.Operation(folder, CancellationToken.None) is not null)
        {
            return;
        }

        string? current = await Current(folder, CancellationToken.None);

        if (position.Branch is not null)
        {
            if (current != position.Branch)
            {
                await Switch(folder, position.Branch);
            }
        }
        else if (current is not null || await Text(folder, ["rev-parse", "HEAD"], CancellationToken.None) != position.Head)
        {
            await Changed(folder, ["checkout", "--no-overwrite-ignore", "--detach", position.Head]);
        }
    }

    // A command that changes the files is never killed because the action was cancelled, which would leave the index locked and the tree half switched.
    private Task<GitOutput> Change(string folder, IReadOnlyList<string> arguments) => git.Run(folder, arguments, CancellationToken.None, changeTimeout);

    private async Task<bool> Changed(string folder, IReadOnlyList<string> arguments) => (await Change(folder, arguments)).ExitCode == 0;

    private async Task<bool> Push(string folder, string branch, CancellationToken cancellation) => !await HasRemote(folder, cancellation) || await Ok(folder, ["push", "origin", $"refs/heads/{branch}:refs/heads/{branch}"], cancellation);

    // The push replaces only the commit that was looked at, so nothing pushed meanwhile is lost.
    private async Task<bool> PushRewritten(string folder, string branch, string? published, CancellationToken cancellation)
    {
        if (!await HasRemote(folder, cancellation))
        {
            return true;
        }

        if (published is not null)
        {
            await safeguards.Keep(folder, $"remote/{branch}", published);
        }

        return await Ok(folder, ["push", "origin", $"--force-with-lease=refs/heads/{branch}:{published}", $"refs/heads/{branch}:refs/heads/{branch}"], cancellation);
    }

    // The commit the remote has for a branch as far as it can be asked now, or as last seen; null when it has none.
    private async Task<string?> Published(string folder, string branch, CancellationToken cancellation)
    {
        if (!await HasRemote(folder, cancellation))
        {
            return null;
        }

        if (!await Ok(folder, ["fetch", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], cancellation) && (await git.Run(folder, ["ls-remote", "--exit-code", "origin", $"refs/heads/{branch}"], cancellation)).ExitCode == 2)
        {
            return null;
        }

        return await Remote(folder, branch, cancellation);
    }

    // Rewriting a branch may only replace commits whose changes the branch still holds, as after a message was amended or the commits were rebased, or what the server rewrote without yet pushing it; anything else is someone's work.
    private async Task RequireIncluded(string folder, string branch, string? published, CancellationToken cancellation)
    {
        if (published is null || await Ok(folder, ["merge-base", "--is-ancestor", published, $"refs/heads/{branch}"], cancellation))
        {
            return;
        }

        string marker = Unpushed(branch);

        if (await Text(folder, ["rev-parse", "--verify", "--quiet", $"{marker}/replaced"], cancellation) == published
            && await Text(folder, ["rev-parse", "--verify", "--quiet", $"{marker}/rewritten"], cancellation) is { Length: > 0 } rewritten
            && await Ok(folder, ["merge-base", "--is-ancestor", rewritten, $"refs/heads/{branch}"], cancellation))
        {
            return;
        }

        if (await HoldsTheChanges(folder, $"refs/heads/{branch}", published, cancellation))
        {
            return;
        }

        throw new RequestException($"The remote has commits on '{branch}' whose changes your branch does not hold, which this would erase. Bring them into it first, or if you dropped them on purpose, force-push the branch yourself.");
    }

    // Whether the branch has a commit with the same change for each one only the other side has; nothing can say that of a merge.
    private async Task<bool> HoldsTheChanges(string folder, string branch, string other, CancellationToken cancellation)
    {
        // Git marks each commit the other side has with a plus unless the branch has the same change.
        GitOutput compared = await git.Run(folder, ["cherry", branch, other], cancellation);

        if (compared.ExitCode != 0 || Lines(compared.Output).Any(line => line.StartsWith('+')) || await Number(folder, ["rev-list", "--count", "--merges", $"{branch}..{other}"], cancellation) > 0)
        {
            return false;
        }

        // To git a change is the same whatever its white space, which is not so for every language, so each one is compared exactly as well.
        foreach (string commit in Lines(await Raw(folder, ["rev-list", $"{branch}..{other}"], cancellation)))
        {
            string patch = await Patch(folder, commit, cancellation);
            string[] paths = [.. Paths(await Raw(folder, ["diff-tree", "-r", "--root", "--no-commit-id", "--no-renames", "--name-only", "-z", commit], cancellation)).Select(path => $":(literal){path}")];
            bool found = false;

            foreach (string candidate in Lines(await Raw(folder, ["rev-list", "--no-merges", $"{other}..{branch}", "--", .. paths], cancellation)))
            {
                if (await Patch(folder, candidate, cancellation) == patch)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    // The change of a commit without what differs only because of where it was applied: blob names and line numbers.
    private async Task<string> Patch(string folder, string commit, CancellationToken cancellation)
        => string.Join('\n', (await Raw(folder, ["diff-tree", "-r", "--root", "--no-commit-id", "--no-renames", "--no-ext-diff", "--no-textconv", "--binary", "-p", commit], cancellation))
            .Split('\n')
            .Where(line => !line.StartsWith("index ", StringComparison.Ordinal))
            .Select(line => line.StartsWith("@@ ", StringComparison.Ordinal) ? "@@" : line));

    // The name is one part of the reference, so a branch and one nested under its name keep markers apart.
    private string Unpushed(string branch) => $"refs/auto-remote/unpushed/{Uri.EscapeDataString(branch)}";

    // Kept until the rewritten branch is pushed: the commit of the remote that the rewrite replaces, and the rewrite.
    private async Task Remember(string folder, string branch, string? published)
    {
        if (published is not null)
        {
            await git.Run(folder, ["update-ref", $"{Unpushed(branch)}/replaced", published], CancellationToken.None);
            await git.Run(folder, ["update-ref", $"{Unpushed(branch)}/rewritten", await Tip(folder, branch, CancellationToken.None)], CancellationToken.None);
        }
    }

    private async Task Forget(string folder, string branch)
    {
        await git.Run(folder, ["update-ref", "-d", $"{Unpushed(branch)}/replaced"], CancellationToken.None);
        await git.Run(folder, ["update-ref", "-d", $"{Unpushed(branch)}/rewritten"], CancellationToken.None);
    }

    private async Task<string[]> CheckoutArguments(string folder, string name, CancellationToken cancellation)
    {
        RequireName(name);

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], cancellation))
        {
            return ["checkout", "--no-overwrite-ignore", name, "--"];
        }

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/remotes/origin/{name}"], cancellation))
        {
            return ["checkout", "--no-overwrite-ignore", "-b", name, "--track", $"refs/remotes/origin/{name}"];
        }

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/tags/{name}"], cancellation) || (new Regex("^[0-9a-fA-F]{4,64}$").IsMatch(name) && await Ok(folder, ["rev-parse", "--verify", "--quiet", $"{name}^{{commit}}"], cancellation)))
        {
            return ["checkout", "--no-overwrite-ignore", "--detach", name];
        }

        throw new RequestException($"There is no branch, tag or commit named '{name}'.");
    }

    private async Task RequireNothingInTheWay(string folder, string tree, CancellationToken cancellation)
    {
        if (await safeguards.InTheWay(folder, tree, null, cancellation) is { Count: > 0 } found)
        {
            throw new RequestException($"Checking out would overwrite files that git does not track: {string.Join(", ", found)}. Move them away first.");
        }
    }

    private async Task Local(string folder, string name, CancellationToken cancellation)
    {
        RequireName(name);

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], cancellation))
        {
            return;
        }

        if (!await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/remotes/origin/{name}"], cancellation) || !await Ok(folder, ["branch", name, $"refs/remotes/origin/{name}"], cancellation))
        {
            throw new RequestException($"There is no branch named '{name}'.");
        }
    }

    private Task<int> Count(string folder, string branch, string baseBranch, CancellationToken cancellation) => Number(folder, ["rev-list", "--count", $"refs/heads/{baseBranch}..refs/heads/{branch}"], cancellation);

    private async Task<int> Number(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation) => int.TryParse(await Text(folder, arguments, cancellation), out int count) ? count : 0;

    // Full reference names throughout: a tag may bear the name of a branch, and git would then take the tag.
    private Task<bool> Contains(string folder, string branch, string other, CancellationToken cancellation) => Ok(folder, ["merge-base", "--is-ancestor", $"refs/heads/{other}", $"refs/heads/{branch}"], cancellation);

    private Task<string> Tip(string folder, string branch, CancellationToken cancellation) => Text(folder, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellation);

    private async Task<IReadOnlyList<string>> Branches(string folder, CancellationToken cancellation)
    {
        HashSet<string> names = [.. Lines(await Text(folder, ["for-each-ref", "--format=%(refname:lstrip=2)", "refs/heads"], cancellation))];
        return [.. names];
    }

    private async Task<string?> Remote(string folder, string branch, CancellationToken cancellation) => await Text(folder, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], cancellation) is { Length: > 0 } tip ? tip : null;

    private async Task<string?> Current(string folder, CancellationToken cancellation) => await Text(folder, ["symbolic-ref", "-q", "HEAD"], cancellation) is { Length: > 0 } name ? name["refs/heads/".Length..] : null;

    private async Task<bool> HasRemote(string folder, CancellationToken cancellation) => await Ok(folder, ["remote", "get-url", "origin"], cancellation);

    private async Task<bool> OnRemote(string folder, string reference, CancellationToken cancellation) => await HasRemote(folder, cancellation) && (await Text(folder, ["ls-remote", "origin", reference], cancellation)).Split('\n').Any(line => line.EndsWith($"\t{reference}", StringComparison.Ordinal));

    private async Task<bool> Pending(string folder, CancellationToken cancellation) => (await Text(folder, ["status", "--porcelain"], cancellation)).Length > 0;

    private async Task RequireNoOperation(string folder, CancellationToken cancellation)
    {
        // Whatever fails here is undone by aborting, which must never reach what the user paused.
        if (await safeguards.Operation(folder, cancellation) is { } operation)
        {
            throw new RequestException($"A git {operation} is in progress in this repository. Finish or abort it first.");
        }
    }

    private async Task RequireClean(string folder, CancellationToken cancellation)
    {
        if (await Pending(folder, cancellation))
        {
            throw new RequestException("Commit or reset your changes first - this needs a clean working tree.");
        }
    }

    private async Task<bool> Ok(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation) => (await git.Run(folder, arguments, cancellation)).ExitCode == 0;

    private async Task<string> Text(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, arguments, cancellation);
        return result.ExitCode == 0 ? result.Output.Trim() : string.Empty;
    }

    private async Task<string> Raw(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await git.Run(folder, arguments, cancellation);
        return result.ExitCode == 0 ? result.Output : string.Empty;
    }

    private IReadOnlyList<string> Paths(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private IReadOnlyList<string> Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void Check(GitOutput result)
    {
        if (result.ExitCode != 0)
        {
            throw new RequestException(Failure(result));
        }
    }

    private string Failure(GitOutput result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim() is { Length: > 0 } text ? text : "Git failed.";

    private void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name.Any(char.IsControl) || name.Contains(' '))
        {
            throw new RequestException($"'{name}' is not a valid name.");
        }
    }

    private void RequireMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new RequestException("Enter a commit message.");
        }
    }

    private void Publish(Guid id) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = id });

    private async Task<Snapshot> Capture(string folder, CancellationToken cancellation)
    {
        Dictionary<string, string> tips = [];

        foreach (string line in Lines(await Text(folder, ["for-each-ref", "--format=%(refname:lstrip=2) %(objectname)", "refs/heads"], cancellation)))
        {
            int separator = line.LastIndexOf(' ');

            if (separator > 0)
            {
                tips[line[..separator]] = line[(separator + 1)..];
            }
        }

        return new Snapshot(await Current(folder, cancellation), await Text(folder, ["rev-parse", "HEAD"], cancellation), await Pending(folder, cancellation), tips, await safeguards.Operation(folder, cancellation));
    }

    // Undoing never discards: it uses no hard reset and removes no file, since changes and untracked or ignored files may be the user's.
    private async Task<bool> Undo(string folder, Snapshot? snapshot, Journal journal)
    {
        // What was paused before the action began is the user's, and what was pushed cannot be taken back here.
        if (snapshot is null || snapshot.Operation is not null || journal.Published)
        {
            return true;
        }

        CancellationToken none = CancellationToken.None;

        if (!await Abort(folder, journal))
        {
            return false;
        }

        bool back = await GoBack(folder, snapshot, none);

        foreach (string branch in journal.Written.Distinct())
        {
            if (!snapshot.Tips.TryGetValue(branch, out string? tip))
            {
                continue;
            }

            string now = await Tip(folder, branch, none);

            if (now == tip)
            {
                continue;
            }

            await safeguards.Keep(folder, branch, now);

            if (await Current(folder, none) == branch)
            {
                await Move(folder, tip, snapshot.Pending, none);
            }
            else if (now.Length == 0)
            {
                await git.Run(folder, ["branch", "--", branch, tip], none);
            }
            else
            {
                await git.Run(folder, ["branch", "-f", branch, tip], none);
            }
        }

        // No branch is checked out here, so moving the head moves none.
        if (back && snapshot.Branch is null && snapshot.Head.Length > 0)
        {
            await Move(folder, snapshot.Head, snapshot.Pending, none);
        }

        return back;
    }

    // Aborts what an action, or the AI resolving for it, left paused; false when that could lose files, so it stays paused.
    private async Task<bool> Abort(string folder, Journal journal)
    {
        CancellationToken none = CancellationToken.None;

        if (await safeguards.Operation(folder, none) is not { } operation)
        {
            return true;
        }

        if (await Pending(folder, none))
        {
            string? files = await safeguards.Files(folder);

            // Aborting takes every change to tracked files with it, so what someone changed since git stopped is saved first.
            if (files is null || files != journal.Baseline && !await safeguards.Store(folder, files, "Auto Remote: files as they were when a git action was undone"))
            {
                return false;
            }
        }

        // Aborting writes what was checked out before, over any ignored file that has appeared where one of those was.
        string restored = operation == "rebase" && await RebaseStart(folder) is { Length: > 0 } start ? start : "HEAD";

        if ((await safeguards.InTheWay(folder, restored, null, none)).Count > 0)
        {
            return false;
        }

        string head = await Text(folder, ["rev-parse", "HEAD"], none);

        // Something committed while the operation was paused would be reached by nothing afterwards.
        if (head.Length > 0 && (await Text(folder, ["for-each-ref", "--contains", head, "--count=1", "refs/heads", "refs/tags", "refs/remotes"], none)).Length == 0)
        {
            await safeguards.Keep(folder, "aborted", head);
        }

        await Change(folder, [operation, "--abort"]);
        return await safeguards.Operation(folder, none) is null;
    }

    private async Task<string?> RebaseStart(string folder)
    {
        foreach (string state in new[] { "rebase-merge", "rebase-apply" })
        {
            string file = Path.Combine(folder, await Text(folder, ["rev-parse", "--git-path", $"{state}/orig-head"], CancellationToken.None));

            if (File.Exists(file))
            {
                return (await File.ReadAllTextAsync(file)).Trim();
            }
        }

        return null;
    }

    private async Task<bool> GoBack(string folder, Snapshot snapshot, CancellationToken cancellation)
    {
        string? current = await Current(folder, cancellation);

        // Letting go of a branch before the head moves keeps the branch where it is.
        if (snapshot.Branch is null)
        {
            return current is null || await Changed(folder, ["checkout", "--detach"]);
        }

        if (current == snapshot.Branch || await Switch(folder, snapshot.Branch))
        {
            return true;
        }

        // The tree was clean before the action, so the edits in the way are its own; they are kept in the stash all the same.
        return !snapshot.Pending && (await safeguards.InTheWay(folder, "HEAD", null, CancellationToken.None)).Count == 0 && await Changed(folder, ["stash", "push", "-m", "Auto Remote: changes left when a git action was undone"]) && await Switch(folder, snapshot.Branch);
    }

    private async Task Move(string folder, string commit, bool keepFiles, CancellationToken cancellation)
    {
        string head = await Text(folder, ["rev-parse", "HEAD"], cancellation);

        if (head == commit)
        {
            return;
        }

        await safeguards.Keep(folder, "head", head);

        if (!keepFiles && (await safeguards.InTheWay(folder, commit, null, cancellation)).Count == 0 && await Changed(folder, ["reset", "--keep", commit]))
        {
            return;
        }

        await Change(folder, ["reset", "--mixed", commit]);
    }

    private enum Outcome
    {
        Succeeded,
        Conflicts,
        Failed,
    }

    private sealed record Running(CancellationTokenSource Source, bool Background, TaskCompletionSource Done);

    private sealed record Snapshot(string? Branch, string Head, bool Pending, IReadOnlyDictionary<string, string> Tips, string? Operation);

    private sealed record Position(string? Branch, string Head);

    private sealed class Journal
    {
        public List<string> Written { get; } = [];

        public bool Published { get; set; }

        public string? Baseline { get; set; }
    }
}
