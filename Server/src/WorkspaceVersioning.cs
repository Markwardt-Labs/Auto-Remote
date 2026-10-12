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
    void Cancel(Guid id);

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
    /// Fetches the remote with pruning and moves the local branches that have nothing of their own to match it; does nothing without a remote.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the action.</param>
    Task Fetch(WorkspaceInfo workspace, CancellationToken cancellation);

    /// <summary>
    /// Fetches, then fast-forwards the checked out branch onto its remote counterpart, stashing and restoring changes that are not committed; the AI resolves conflicts. Does nothing without a remote, while the AI works or another action runs.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="cancellation">Cancels the action.</param>
    /// <exception cref="RequestException">The pull or the restore of the changes failed.</exception>
    Task Sync(WorkspaceInfo workspace, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class WorkspaceVersioning(IGitService git, IPromptService prompts, IEventHub events) : IWorkspaceVersioning
{
    private readonly ConcurrentDictionary<Guid, Running> running = new();

    private readonly int attempts = 3;

    private readonly TimeSpan aiTimeout = TimeSpan.FromMinutes(20);

    private readonly TimeSpan aiPoll = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public bool IsBusy(Guid id) => running.TryGetValue(id, out Running? current) && !current.Background;

    /// <inheritdoc />
    public void Cancel(Guid id)
    {
        if (running.TryGetValue(id, out Running? current))
        {
            current.Source.Cancel();
        }
    }

    /// <inheritdoc />
    public async Task Perform(WorkspaceInfo workspace, bool revert, Func<CancellationToken, Task> action, CancellationToken cancellation, bool background = false)
    {
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        if (!running.TryAdd(workspace.Id, new Running(source, background)))
        {
            throw new RequestException("A git action is already running in this workspace.");
        }

        string before = background ? await Fingerprint(workspace.Folder) : string.Empty;

        if (!background)
        {
            Publish(workspace.Id);
        }

        try
        {
            Snapshot? snapshot = revert ? await Capture(workspace.Folder, CancellationToken.None) : null;

            try
            {
                await action(source.Token);
            }
            catch (OperationCanceledException)
            {
                await Undo(workspace.Folder, snapshot);
                cancellation.ThrowIfCancellationRequested();
                throw new RequestException("The git action was cancelled and undone.");
            }
            catch (Exception exception) when (exception is not RequestException)
            {
                await Undo(workspace.Folder, snapshot);
                throw;
            }
        }
        finally
        {
            running.TryRemove(workspace.Id, out _);

            if (!background || await Fingerprint(workspace.Folder) != before)
            {
                Publish(workspace.Id);
            }
        }
    }

    /// <inheritdoc />
    public Task Checkout(WorkspaceInfo workspace, string name, bool discard, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
        {
            string folder = workspace.Folder;
            string[] arguments = await CheckoutArguments(folder, name, token);

            if (await Pending(folder, token))
            {
                if (!discard)
                {
                    throw new RequestException("Checking out would discard your changes that are not committed.");
                }

                await Discard(folder, token);
            }

            Check(await git.Run(folder, arguments, token));
        },
        cancellation);

    /// <inheritdoc />
    public Task DeleteBranch(WorkspaceInfo workspace, string name, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
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
                Check(await git.Run(folder, ["branch", "-D", name], token));
            }

            if (await OnRemote(folder, "heads", name, token) && !await Ok(folder, ["push", "origin", "--delete", name], token))
            {
                throw new RequestException($"Deleted '{name}' locally, but deleting it on the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task DeleteTag(WorkspaceInfo workspace, string name, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
        {
            string folder = workspace.Folder;
            RequireName(name);

            if (!await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/tags/{name}"], token))
            {
                throw new RequestException($"There is no tag named '{name}'.");
            }

            Check(await git.Run(folder, ["tag", "-d", name], token));

            if (await OnRemote(folder, "tags", name, token) && !await Ok(folder, ["push", "origin", "--delete", $"refs/tags/{name}"], token))
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
            IsBased = await Ok(folder, ["merge-base", "--is-ancestor", baseBranch, branch], cancellation),
            Subject = await Text(folder, ["log", "-1", "--format=%s", $"refs/heads/{branch}"], cancellation),
        };
    }

    /// <inheritdoc />
    public Task Squash(WorkspaceInfo workspace, string branch, string baseBranch, string message, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
        {
            string folder = workspace.Folder;
            RequireMessage(message);
            await Local(folder, branch, token);
            await Local(folder, baseBranch, token);

            if (await Count(folder, branch, baseBranch, token) == 0)
            {
                throw new RequestException($"'{branch}' has no commits that aren't already on '{baseBranch}'.");
            }

            if (!await SquashUnique(folder, branch, baseBranch, message, token))
            {
                throw new RequestException($"Squashing '{branch}' failed - it was left unchanged.");
            }

            if (!await Push(folder, branch, true, token))
            {
                throw new RequestException($"Squashed '{branch}' locally, but force-pushing it to the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task Rebase(WorkspaceInfo workspace, string branch, string onto, string? squashMessage, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
        {
            string folder = workspace.Folder;
            await RequireClean(folder, token);
            await Local(folder, branch, token);
            await Local(folder, onto, token);
            int count = await Count(folder, branch, onto, token);

            if (count == 0 && await Ok(folder, ["merge-base", "--is-ancestor", onto, branch], token))
            {
                throw new RequestException($"'{branch}' is already up to date with '{onto}'.");
            }

            string? original = await Current(folder, token);

            try
            {
                await RebaseBranch(workspace, branch, onto, count > 1 ? squashMessage : null, token);
            }
            catch (RequestException)
            {
                await Return(folder, original, token);
                throw;
            }

            bool pushed = await Push(folder, branch, true, token);
            await Return(folder, original, token);

            if (!pushed)
            {
                throw new RequestException($"Rebased '{branch}' locally, but force-pushing it to the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task Merge(WorkspaceInfo workspace, string source, string target, string? squashMessage, CancellationToken cancellation) => Perform(
        workspace,
        true,
        async token =>
        {
            string folder = workspace.Folder;
            await RequireClean(folder, token);
            await Local(folder, source, token);
            await Local(folder, target, token);
            int count = await Count(folder, source, target, token);

            if (count == 0)
            {
                throw new RequestException($"'{source}' has no commits that aren't already on '{target}'.");
            }

            bool needsRebase = !await Ok(folder, ["merge-base", "--is-ancestor", target, source], token);
            string? message = count > 1 ? squashMessage : null;
            string? original = await Current(folder, token);

            if (needsRebase || message is not null)
            {
                try
                {
                    await RebaseBranch(workspace, source, target, message, token);
                }
                catch (RequestException)
                {
                    await Return(folder, original, token);
                    throw;
                }
            }

            if (!await FastForward(folder, target, source, token))
            {
                await Return(folder, original, token);
                throw new RequestException($"Couldn't fast-forward '{target}' to '{source}'.");
            }

            if (!await Push(folder, target, false, token))
            {
                throw new RequestException($"Merged into '{target}' locally, but pushing it to the remote failed.");
            }

            Check(await git.Run(folder, ["branch", "-D", source], token));

            if (await OnRemote(folder, "heads", source, token) && !await Ok(folder, ["push", "origin", "--delete", source], token))
            {
                throw new RequestException($"Merged, but deleting '{source}' on the remote failed.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public async Task Fetch(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        if (await HasRemote(workspace.Folder, cancellation))
        {
            await Perform(workspace, false, token => Mirror(workspace.Folder, token), cancellation, true);
        }
    }

    /// <inheritdoc />
    public async Task Sync(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        if (prompts.IsBusy(workspace.Id) || running.ContainsKey(workspace.Id) || !await HasRemote(workspace.Folder, cancellation))
        {
            return;
        }

        await Perform(
            workspace,
            false,
            async token =>
            {
                await Mirror(workspace.Folder, token);
                await Pull(workspace, token);
            },
            cancellation,
            true);
    }

    private async Task<string> Fingerprint(string folder) => await Text(folder, ["for-each-ref", "--format=%(refname) %(objectname)"], CancellationToken.None) + await Text(folder, ["rev-parse", "HEAD"], CancellationToken.None) + await Text(folder, ["status", "--porcelain"], CancellationToken.None);

    private async Task Mirror(string folder, CancellationToken cancellation)
    {
        string? current = await Current(folder, cancellation);
        bool hadTracking = current is not null && await Remote(folder, current, cancellation) is not null;
        HashSet<string> inSync = [];

        foreach (string branch in await Branches(folder, cancellation))
        {
            if (branch != current && await Remote(folder, branch, cancellation) is { } prior && prior == await Text(folder, ["rev-parse", $"refs/heads/{branch}"], cancellation))
            {
                inSync.Add(branch);
            }
        }

        if (!await Ok(folder, ["fetch", "origin", "--tags", "--prune"], cancellation))
        {
            return;
        }

        foreach (string branch in await Branches(folder, cancellation))
        {
            if (branch == current || await Remote(folder, branch, cancellation) is not { } remote)
            {
                continue;
            }

            string local = await Text(folder, ["rev-parse", $"refs/heads/{branch}"], cancellation);

            if (remote != local && (inSync.Contains(branch) || await Ok(folder, ["merge-base", "--is-ancestor", local, remote], cancellation)))
            {
                await git.Run(folder, ["branch", "-f", branch, remote], cancellation);
            }
        }

        if (current is not null && hadTracking && await Remote(folder, current, cancellation) is null)
        {
            string tip = await Text(folder, ["rev-parse", "HEAD"], cancellation);
            await git.Run(folder, ["checkout", "--detach", tip], cancellation);
            await git.Run(folder, ["branch", "-D", current], cancellation);
        }
    }

    private async Task Pull(WorkspaceInfo workspace, CancellationToken cancellation)
    {
        string folder = workspace.Folder;

        if (await Current(folder, cancellation) is not { } branch)
        {
            return;
        }

        string original = await Text(folder, ["rev-parse", "HEAD"], cancellation);
        string? remote = await Remote(folder, branch, cancellation);

        if (remote is null || remote == original || !await Ok(folder, ["merge-base", "--is-ancestor", original, remote], cancellation))
        {
            return;
        }

        bool pending = await Pending(folder, cancellation);

        if (pending && !await Ok(folder, ["stash", "push", "-u"], cancellation))
        {
            throw new RequestException("Stashing your changes to pull failed.");
        }

        if (!await Ok(folder, ["merge", "--ff-only", $"origin/{branch}"], cancellation))
        {
            if (pending)
            {
                await git.Run(folder, ["stash", "pop"], cancellation);
            }

            throw new RequestException($"Pulling '{branch}' failed.");
        }

        if (!pending)
        {
            return;
        }

        Outcome popped = await Classify(folder, await git.Run(folder, ["stash", "pop"], cancellation), cancellation);

        if (popped == Outcome.Failed)
        {
            throw new RequestException("Restoring your changes after the pull failed.");
        }

        if (popped == Outcome.Conflicts)
        {
            Outcome resolved = await Resolve(workspace, popped, _ => Task.FromResult(Outcome.Succeeded), files => StashInstruction(files, original), cancellation);

            if (resolved == Outcome.Conflicts)
            {
                throw new RequestException("Restoring your changes after the pull left conflicts that need resolving by hand; your changes are kept in the stash.");
            }

            await git.Run(folder, ["stash", "drop"], cancellation);
        }
    }

    private async Task RebaseBranch(WorkspaceInfo workspace, string branch, string onto, string? squashMessage, CancellationToken cancellation)
    {
        string folder = workspace.Folder;
        Outcome outcome = await RebaseOnto(folder, branch, onto, squashMessage, cancellation);
        outcome = await Resolve(workspace, outcome, async token => await Classify(folder, await git.Run(folder, ["rebase", "--continue"], token), token), ConflictInstruction, cancellation);

        if (outcome == Outcome.Conflicts)
        {
            await git.Run(folder, ["rebase", "--abort"], cancellation);
            throw new RequestException($"Could not automatically resolve the conflicts rebasing '{branch}' onto '{onto}' - aborted.");
        }

        if (outcome == Outcome.Failed)
        {
            throw new RequestException($"Rebasing '{branch}' onto '{onto}' failed.");
        }
    }

    private async Task<Outcome> RebaseOnto(string folder, string branch, string onto, string? squashMessage, CancellationToken cancellation)
    {
        if (squashMessage is not null && !await SquashUnique(folder, branch, onto, squashMessage, cancellation))
        {
            return Outcome.Failed;
        }

        if (await Ok(folder, ["merge-base", "--is-ancestor", onto, branch], cancellation))
        {
            return Outcome.Succeeded;
        }

        if (!await CheckoutBranch(folder, branch, cancellation))
        {
            return Outcome.Failed;
        }

        return await Classify(folder, await git.Run(folder, ["rebase", onto], cancellation), cancellation);
    }

    private async Task<Outcome> Resolve(WorkspaceInfo workspace, Outcome outcome, Func<CancellationToken, Task<Outcome>> proceed, Func<IReadOnlyList<string>, string> instruction, CancellationToken cancellation)
    {
        string folder = workspace.Folder;

        for (int attempt = 0; outcome == Outcome.Conflicts && attempt < attempts; attempt++)
        {
            IReadOnlyList<string> files = await Conflicted(folder, cancellation);

            try
            {
                await prompts.Prompt(workspace.Id, instruction(files), cancellation);
            }
            catch (RequestException)
            {
                return outcome;
            }

            await Idle(workspace.Id, cancellation);

            if ((await Conflicted(folder, cancellation)).Count > 0)
            {
                continue;
            }

            outcome = await proceed(cancellation);
        }

        return outcome;
    }

    private async Task Idle(Guid id, CancellationToken cancellation)
    {
        DateTime limit = DateTime.UtcNow + aiTimeout;

        while (prompts.IsBusy(id) && DateTime.UtcNow < limit)
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
        string mergeBase = await Text(folder, ["merge-base", baseBranch, branch], cancellation);
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
        if (!await Ok(folder, ["merge-base", "--is-ancestor", target, source], cancellation))
        {
            return false;
        }

        string? original = await Current(folder, cancellation);

        if (!await CheckoutBranch(folder, target, cancellation))
        {
            return false;
        }

        if (await Ok(folder, ["merge", "--ff-only", source], cancellation))
        {
            return true;
        }

        await Return(folder, original, cancellation);
        return false;
    }

    private async Task<bool> CheckoutBranch(string folder, string branch, CancellationToken cancellation)
    {
        if (await Current(folder, cancellation) != branch)
        {
            await git.Run(folder, ["checkout", branch], cancellation);
        }

        return await Current(folder, cancellation) == branch;
    }

    private async Task Return(string folder, string? branch, CancellationToken cancellation)
    {
        if (branch is not null && await Current(folder, cancellation) != branch)
        {
            await git.Run(folder, ["checkout", branch], cancellation);
        }
    }

    private async Task<bool> Push(string folder, string branch, bool force, CancellationToken cancellation)
    {
        if (!await HasRemote(folder, cancellation))
        {
            return true;
        }

        return await Ok(folder, force ? ["push", "origin", branch, "--force"] : ["push", "origin", branch], cancellation);
    }

    private async Task<string[]> CheckoutArguments(string folder, string name, CancellationToken cancellation)
    {
        RequireName(name);

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], cancellation))
        {
            return ["checkout", name];
        }

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/remotes/origin/{name}"], cancellation))
        {
            return ["checkout", "-b", name, "--track", $"origin/{name}"];
        }

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/tags/{name}"], cancellation) || (new Regex("^[0-9a-fA-F]{4,64}$").IsMatch(name) && await Ok(folder, ["rev-parse", "--verify", "--quiet", $"{name}^{{commit}}"], cancellation)))
        {
            return ["checkout", "--detach", name];
        }

        throw new RequestException($"There is no branch, tag or commit named '{name}'.");
    }

    private async Task Local(string folder, string name, CancellationToken cancellation)
    {
        RequireName(name);

        if (await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], cancellation))
        {
            return;
        }

        if (!await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/remotes/origin/{name}"], cancellation) || !await Ok(folder, ["branch", name, $"origin/{name}"], cancellation))
        {
            throw new RequestException($"There is no branch named '{name}'.");
        }
    }

    private async Task<int> Count(string folder, string branch, string baseBranch, CancellationToken cancellation) => int.TryParse(await Text(folder, ["rev-list", "--count", $"refs/heads/{baseBranch}..refs/heads/{branch}"], cancellation), out int count) ? count : 0;

    private async Task<IReadOnlyList<string>> Branches(string folder, CancellationToken cancellation)
    {
        HashSet<string> names = [.. Lines(await Text(folder, ["for-each-ref", "--format=%(refname:short)", "refs/heads"], cancellation))];
        return [.. names];
    }

    private async Task<string?> Remote(string folder, string branch, CancellationToken cancellation) => await Text(folder, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], cancellation) is { Length: > 0 } tip ? tip : null;

    private async Task<string?> Current(string folder, CancellationToken cancellation) => await Text(folder, ["symbolic-ref", "--short", "-q", "HEAD"], cancellation) is { Length: > 0 } name ? name : null;

    private async Task<bool> HasRemote(string folder, CancellationToken cancellation) => await Ok(folder, ["remote", "get-url", "origin"], cancellation);

    private async Task<bool> OnRemote(string folder, string kind, string name, CancellationToken cancellation) => await HasRemote(folder, cancellation) && await Ok(folder, ["ls-remote", "--exit-code", $"--{kind}", "origin", name], cancellation);

    private async Task<bool> Pending(string folder, CancellationToken cancellation) => (await Text(folder, ["status", "--porcelain"], cancellation)).Length > 0;

    private async Task Discard(string folder, CancellationToken cancellation)
    {
        await git.Run(folder, ["reset", "--hard"], cancellation);
        await git.Run(folder, ["clean", "-fd"], cancellation);
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

    private IReadOnlyList<string> Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void Check(GitOutput result)
    {
        if (result.ExitCode != 0)
        {
            throw new RequestException((result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim() is { Length: > 0 } text ? text : "Git failed.");
        }
    }

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

        foreach (string line in Lines(await Text(folder, ["for-each-ref", "--format=%(refname:short) %(objectname)", "refs/heads"], cancellation)))
        {
            int separator = line.LastIndexOf(' ');

            if (separator > 0)
            {
                tips[line[..separator]] = line[(separator + 1)..];
            }
        }

        return new Snapshot(await Current(folder, cancellation), await Text(folder, ["rev-parse", "HEAD"], cancellation), await Pending(folder, cancellation), tips);
    }

    private async Task Undo(string folder, Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        CancellationToken none = CancellationToken.None;

        if ((await Conflicted(folder, none)).Count > 0)
        {
            await git.Run(folder, ["rebase", "--abort"], none);
            await git.Run(folder, ["merge", "--abort"], none);
        }

        if (snapshot.Branch is not null && await Current(folder, none) != snapshot.Branch)
        {
            if (!snapshot.Pending)
            {
                await Discard(folder, none);
            }

            await git.Run(folder, ["checkout", snapshot.Branch], none);
        }

        if (snapshot.Head.Length > 0)
        {
            if (snapshot.Pending)
            {
                await git.Run(folder, ["reset", "--mixed", snapshot.Head], none);
            }
            else
            {
                await git.Run(folder, ["reset", "--hard", snapshot.Head], none);
                await git.Run(folder, ["clean", "-fd"], none);
            }
        }

        foreach ((string branch, string tip) in snapshot.Tips.Where(pair => pair.Key != snapshot.Branch))
        {
            if (!await Ok(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], none))
            {
                await git.Run(folder, ["branch", "--", branch, tip], none);
            }
            else if (await Text(folder, ["rev-parse", $"refs/heads/{branch}"], none) != tip)
            {
                await git.Run(folder, ["branch", "-f", branch, tip], none);
            }
        }
    }

    private enum Outcome
    {
        Succeeded,
        Conflicts,
        Failed,
    }

    private sealed record Running(CancellationTokenSource Source, bool Background);

    private sealed record Snapshot(string? Branch, string Head, bool Pending, IReadOnlyDictionary<string, string> Tips);
}
