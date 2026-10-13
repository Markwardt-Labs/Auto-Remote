namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The git state of one workspace as the server reports it, what differs in its working tree, and the controls that make the server commit, discard, branch, tag or set the remote; git itself only ever runs on the server.
/// </summary>
internal sealed class GitViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="syncInterval">How often the checked out branch is fetched and pulled while the workspace is focused, or <see langword="null" /> for a minute.</param>
    public GitViewModel(IServerConnection connection, Guid workspaceId, TimeSpan? syncInterval = null)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.syncInterval = syncInterval ?? TimeSpan.FromSeconds(60);
        History = new GitHistoryViewModel(connection, workspaceId, this);
        CommitCommand = new AsyncCommand(_ => AskCommit(), () => CanAct);
        ResetCommand = new AsyncCommand(_ => AskReset(), () => CanAct);
        BranchCommand = new AsyncCommand(_ => NewBranch(), () => CanAct);
        TagCommand = new AsyncCommand(_ => NewTag(), () => CanAct);
        RemoteCommand = new AsyncCommand(_ => SetRemote(), () => CanAct);
        FetchCommand = new AsyncCommand(_ => Fetch(), () => CanAct);
        ConfirmCommand = new AsyncCommand(_ => Confirm());
        CancelCommand = new AsyncCommand(_ =>
        {
            Prompt = GitPrompt.None;
            confirmAction = null;
            afterwards = null;
            Error = null;
            return Task.CompletedTask;
        });
        CancelWorkCommand = new AsyncCommand(async _ =>
        {
            try
            {
                await connection.GitCancel(workspaceId, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
            {
            }
        });
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IsBusy))
            {
                Raise(nameof(IsWorking));
                RefreshActions();
            }
        };
        ToggleChangesModeCommand = new AsyncCommand(_ =>
        {
            IsChangesMode = !isChangesMode;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly List<GitChange> changes = [];

    private GitStatus status = new() { IsRepo = false };

    private readonly TimeSpan syncInterval;

    private GitPrompt prompt;

    private string promptTitle = string.Empty;

    private string promptHint = string.Empty;

    private string promptConfirm = string.Empty;

    private string promptText = string.Empty;

    private string promptEmail = string.Empty;

    private string emptyMessage = string.Empty;

    private bool isDestructive;

    private Func<Task>? confirmAction;

    private Func<Task>? afterwards;

    private CancellationTokenSource? syncing;

    private bool isChangesMode;

    private bool isLocked;

    private bool subscribed;

    private bool refreshing;

    private bool again;

    /// <summary>
    /// Raised after the status or the changes were read again.
    /// </summary>
    public event Action? Updated;

    /// <summary>
    /// Raised with a path and its commit, or <see langword="null" /> for the working tree, when the diff of a changed file is asked for.
    /// </summary>
    public event Action<string, string?>? DiffRequested;

    /// <summary>
    /// Gets the history of the repository.
    /// </summary>
    public GitHistoryViewModel History { get; }

    /// <summary>
    /// Gets the tree of the changed paths, ignored ones left out.
    /// </summary>
    public ObservableCollection<ChangeNode> ChangeTree { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the workspace's folder is a git repository.
    /// </summary>
    public bool IsRepo => status.IsRepo;

    /// <summary>
    /// Gets the branch name, the tag name when the head is not on a branch, or a note for an empty repository.
    /// </summary>
    public string Title => status.Branch ?? status.Tag ?? (status.Commit.Length == 0 ? "No commits" : "Detached head");

    /// <summary>
    /// Gets the tag at the head when it is shown next to the branch.
    /// </summary>
    public string? Tag => status.Branch is not null ? status.Tag : null;

    /// <summary>
    /// Gets a value indicating whether a tag is shown next to the branch.
    /// </summary>
    public bool HasTag => Tag is not null;

    /// <summary>
    /// Gets the checked out branch, or <see langword="null" /> when the head is not on a branch.
    /// </summary>
    public string? CurrentBranch => status.Branch;

    /// <summary>
    /// Gets the subject of the head commit.
    /// </summary>
    public string Message => status.Message;

    /// <summary>
    /// Gets the abbreviated hash of the head commit.
    /// </summary>
    public string Commit => status.Commit;

    /// <summary>
    /// Gets a value indicating whether the working tree has changes that are not committed.
    /// </summary>
    public bool HasChanges => status.HasChanges;

    /// <summary>
    /// Gets the remote URL, or an empty string when there is none.
    /// </summary>
    public string Remote => status.Remote ?? string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the file tree shows the changed files instead of every file.
    /// </summary>
    public bool IsChangesMode
    {
        get => isChangesMode;
        set => Set(ref isChangesMode, value && status.IsRepo);
    }

    /// <summary>
    /// Gets a value indicating whether the server is doing git work in the workspace that the user waits for, started from this client or any other.
    /// </summary>
    public bool IsWorking => IsBusy || status.Busy;

    /// <summary>
    /// Gets or sets a value indicating whether other work, the AI or a script, holds the workspace, which keeps git actions from starting.
    /// </summary>
    public bool IsLocked
    {
        get => isLocked;
        set
        {
            if (Set(ref isLocked, value))
            {
                RefreshActions();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a git action can start: nothing else holds the workspace and no git action is running.
    /// </summary>
    public bool CanAct => !isLocked && !IsWorking;

    /// <summary>
    /// Gets the shape of the question being asked.
    /// </summary>
    public GitPrompt Prompt
    {
        get => prompt;
        private set
        {
            if (Set(ref prompt, value))
            {
                Raise(nameof(IsPrompting));
                Raise(nameof(HasInput));
                Raise(nameof(HasEmail));
                Raise(nameof(ShowCancel));
                Raise(nameof(PromptPlaceholder));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a question is being asked.
    /// </summary>
    public bool IsPrompting => prompt != GitPrompt.None;

    /// <summary>
    /// Gets a value indicating whether the question needs text typed.
    /// </summary>
    public bool HasInput => prompt is GitPrompt.Input or GitPrompt.Identity;

    /// <summary>
    /// Gets a value indicating whether the question also needs an email typed.
    /// </summary>
    public bool HasEmail => prompt == GitPrompt.Identity;

    /// <summary>
    /// Gets a value indicating whether the question can be backed out of, which a notice cannot.
    /// </summary>
    public bool ShowCancel => prompt is not (GitPrompt.None or GitPrompt.Notice);

    /// <summary>
    /// Gets a value indicating whether confirming the question destroys work.
    /// </summary>
    public bool IsDestructive => isDestructive;

    /// <summary>
    /// Gets or sets the text typed for the question, the name for an identity.
    /// </summary>
    public string PromptText
    {
        get => promptText;
        set => Set(ref promptText, value);
    }

    /// <summary>
    /// Gets or sets the email typed for an identity.
    /// </summary>
    public string PromptEmail
    {
        get => promptEmail;
        set => Set(ref promptEmail, value);
    }

    /// <summary>
    /// Gets the title of the question.
    /// </summary>
    public string PromptTitle => promptTitle;

    /// <summary>
    /// Gets the explanation of the question, or the placeholder of the text typed for it.
    /// </summary>
    public string PromptHint => promptHint;

    /// <summary>
    /// Gets the placeholder of the text typed for the question.
    /// </summary>
    public string PromptPlaceholder => prompt == GitPrompt.Identity ? "Name" : promptHint;

    /// <summary>
    /// Gets the label of the button that answers the question.
    /// </summary>
    public string PromptConfirm => promptConfirm;

    /// <summary>
    /// Gets the command that asks for a commit message.
    /// </summary>
    public AsyncCommand CommitCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to discard the changes.
    /// </summary>
    public AsyncCommand ResetCommand { get; }

    /// <summary>
    /// Gets the command that asks for the name of a new branch.
    /// </summary>
    public AsyncCommand BranchCommand { get; }

    /// <summary>
    /// Gets the command that asks for the name of a new tag.
    /// </summary>
    public AsyncCommand TagCommand { get; }

    /// <summary>
    /// Gets the command that asks for the remote URL.
    /// </summary>
    public AsyncCommand RemoteCommand { get; }

    /// <summary>
    /// Gets the command that carries out the question being asked.
    /// </summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>
    /// Gets the command that drops the question.
    /// </summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// Gets the command that fetches the remote with pruning.
    /// </summary>
    public AsyncCommand FetchCommand { get; }

    /// <summary>
    /// Gets the command that cancels the git work the server is doing, which undoes what it had done.
    /// </summary>
    public AsyncCommand CancelWorkCommand { get; }

    /// <summary>
    /// Gets the command that switches the file tree between every file and the changed files.
    /// </summary>
    public AsyncCommand ToggleChangesModeCommand { get; }

    /// <summary>
    /// Reads the state again and starts following the events that say it may have changed.
    /// </summary>
    /// <returns>A task that completes once the state is read.</returns>
    public Task Start()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
        }

        return Refresh();
    }

    /// <summary>
    /// Reads the status and the changes again; a read asked for while one runs makes it run once more.
    /// </summary>
    /// <returns>A task that completes once the state is read.</returns>
    public async Task Refresh()
    {
        if (refreshing)
        {
            again = true;
            return;
        }

        refreshing = true;

        try
        {
            do
            {
                again = false;
                await Read();
            }
            while (again);
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>
    /// Asks for the diff of a changed file.
    /// </summary>
    /// <param name="path">The file relative to the workspace folder.</param>
    /// <param name="commit">The commit, or <see langword="null" /> for the working tree.</param>
    public void RequestDiff(string path, string? commit) => DiffRequested?.Invoke(path, commit);

    /// <summary>
    /// Finds how git sees a path of the file tree.
    /// </summary>
    /// <param name="path">The path relative to the workspace folder.</param>
    /// <param name="isFolder">Whether the path is a folder.</param>
    /// <returns>The status, or <see langword="null" /> for a path without changes.</returns>
    public GitChangeStatus? StatusOf(string path, bool isFolder)
    {
        if (IsRepo && (path == ".git" || path.StartsWith(".git/", StringComparison.Ordinal)))
        {
            return GitChangeStatus.Ignored;
        }

        foreach (GitChange change in changes)
        {
            if (change.Status == GitChangeStatus.Ignored)
            {
                if (change.Path == path || change.Path == path + "/" || (change.Path.EndsWith('/') && path.StartsWith(change.Path, StringComparison.Ordinal)))
                {
                    return GitChangeStatus.Ignored;
                }
            }
            else if (change.Path == path)
            {
                return change.Status;
            }
        }

        if (!isFolder)
        {
            return null;
        }

        string prefix = path + "/";
        List<GitChange> inside = [.. changes.Where(change => change.Status != GitChangeStatus.Ignored && change.Path.StartsWith(prefix, StringComparison.Ordinal))];
        return inside.Count == 0 ? null : inside.All(change => change.Status == GitChangeStatus.Added) ? GitChangeStatus.Added : GitChangeStatus.Modified;
    }

    /// <summary>
    /// Stops following events.
    /// </summary>
    public void Dispose()
    {
        StopSync();

        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId == workspaceId && (remoteEvent.Kind is RemoteEventKind.FilesChanged or RemoteEventKind.GitChanged || (remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == false)))
        {
            _ = Reread(remoteEvent.Kind == RemoteEventKind.GitChanged);
        }
    }

    private async Task Reread(bool refs)
    {
        await Refresh();

        if (refs)
        {
            await History.Changed();
        }
    }

    private async Task Read()
    {
        try
        {
            GitStatus read = await connection.GetGitStatus(workspaceId, CancellationToken.None);
            IReadOnlyList<GitChange> found = read.IsRepo ? await connection.GetGitChanges(workspaceId, CancellationToken.None) : [];
            bool moved = read.Commit != status.Commit || read.Branch != status.Branch || read.Tag != status.Tag || read.IsRepo != status.IsRepo;
            status = read;
            changes.Clear();
            changes.AddRange(found);
            ChangeNode root = new();
            root.Fill(found.Where(change => change.Status != GitChangeStatus.Ignored), null, node => RequestDiff(node.Path, node.Commit));
            ChangeTree.Clear();

            foreach (ChangeNode node in root.Children)
            {
                ChangeTree.Add(node);
            }

            if (!read.IsRepo)
            {
                IsChangesMode = false;
            }

            Notify();
            Updated?.Invoke();

            if (moved)
            {
                await History.Changed();
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private void Notify()
    {
        Raise(nameof(IsRepo));
        Raise(nameof(Title));
        Raise(nameof(Tag));
        Raise(nameof(HasTag));
        Raise(nameof(Message));
        Raise(nameof(Commit));
        Raise(nameof(HasChanges));
        Raise(nameof(Remote));
        Raise(nameof(IsWorking));
        RefreshActions();
    }

    private void RefreshActions()
    {
        Raise(nameof(CanAct));
        CommitCommand.Refresh();
        ResetCommand.Refresh();
        BranchCommand.Refresh();
        TagCommand.Refresh();
        RemoteCommand.Refresh();
        FetchCommand.Refresh();
    }

    private void Ask(GitPrompt kind, string title, string hint, string confirm, Func<Task>? action, string text = "", bool destructive = false, string empty = "", Func<Task>? then = null)
    {
        if (isLocked && kind != GitPrompt.Notice)
        {
            Ask(GitPrompt.Notice, "Git", "Wait for the AI or the script to finish before running git actions.", "OK", null);
            return;
        }

        Error = null;
        promptTitle = title;
        promptHint = hint;
        promptConfirm = confirm;
        isDestructive = destructive;
        emptyMessage = empty;
        confirmAction = action;
        afterwards = then;
        PromptText = text;
        PromptEmail = string.Empty;
        Raise(nameof(PromptTitle));
        Raise(nameof(PromptHint));
        Raise(nameof(PromptPlaceholder));
        Raise(nameof(PromptConfirm));
        Raise(nameof(IsDestructive));
        Prompt = kind;
    }

    private Task Notice(string title, string message)
    {
        Ask(GitPrompt.Notice, title, message, "OK", null);
        return Task.CompletedTask;
    }

    private Task WithIdentity(Func<Task> next)
    {
        if (status.HasIdentity)
        {
            return next();
        }

        Ask(GitPrompt.Identity, "Git identity", "Git needs your name and email to commit.", "Save", null, then: next);
        return Task.CompletedTask;
    }

    private Task AskCommit() => WithIdentity(() =>
    {
        Ask(GitPrompt.Input, "Commit", "Message", "Commit", () => connection.GitCommit(workspaceId, promptText.Trim(), CancellationToken.None), empty: "Enter a commit message.");
        return Task.CompletedTask;
    });

    private Task AskReset()
    {
        Ask(GitPrompt.Confirm, "Reset", "Discard all changes that are not committed, new files included? This cannot be undone.", "Discard", () => connection.GitReset(workspaceId, CancellationToken.None), destructive: true);
        return Task.CompletedTask;
    }

    private Task NewBranch()
    {
        Ask(GitPrompt.Input, "New branch", "Branch name", "Create branch", () => connection.GitCreateBranch(workspaceId, promptText.Trim(), CancellationToken.None), empty: "Enter a name.");
        return Task.CompletedTask;
    }

    private Task NewTag() => WithIdentity(() =>
    {
        Ask(GitPrompt.Input, "New tag", "Tag name", "Create tag", () => connection.GitCreateTag(workspaceId, promptText.Trim(), CancellationToken.None), empty: "Enter a name.");
        return Task.CompletedTask;
    });

    private Task SetRemote()
    {
        Ask(GitPrompt.Input, "Git remote", status.IsRepo ? "Remote URL" : "Remote URL (makes the workspace a git repository)", "Set remote", () => connection.GitSetRemote(workspaceId, promptText.Trim(), CancellationToken.None), Remote, empty: "Enter the remote URL.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks whether to check out a branch, tag or commit; changes that are not committed are discarded when the user agrees.
    /// </summary>
    /// <param name="name">The branch, tag or commit.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task Checkout(string name)
    {
        bool discard = status.HasChanges;
        string shown = name.Length > 12 && name.All(Uri.IsHexDigit) ? name[..7] : name;
        Ask(GitPrompt.Confirm, "Checkout", discard ? $"Check out '{shown}'? This will discard your pending changes." : $"Check out '{shown}'?", discard ? "Discard and checkout" : "Checkout", () => connection.GitCheckout(workspaceId, name, discard, CancellationToken.None), destructive: discard);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks whether to delete a branch locally and on the remote.
    /// </summary>
    /// <param name="name">The branch.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task DeleteBranch(string name)
    {
        Ask(GitPrompt.Confirm, "Delete branch", $"Delete branch '{name}'? This cannot be undone.", "Delete", () => connection.GitDeleteBranch(workspaceId, name, CancellationToken.None), destructive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks whether to delete a tag locally and on the remote.
    /// </summary>
    /// <param name="name">The tag.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task DeleteTag(string name)
    {
        Ask(GitPrompt.Confirm, "Delete tag", $"Delete tag '{name}'? This cannot be undone.", "Delete", () => connection.GitDeleteTag(workspaceId, name, CancellationToken.None), destructive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Squashes the commits of a branch that another lacks into one, asking for the message first.
    /// </summary>
    /// <param name="branch">The branch to squash.</param>
    /// <param name="baseBranch">The branch it is squashed against.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task Squash(string branch, string baseBranch) => WithIdentity(async () =>
    {
        GitBranchState? state = await Compare(branch, baseBranch);

        if (state is null)
        {
            return;
        }

        if (state.Unique == 0)
        {
            await Notice("Squash", $"'{branch}' has no commits that aren't already on '{baseBranch}'.");
            return;
        }

        AskSquash("Squash", branch, baseBranch, state, "Squash", message => connection.GitSquash(workspaceId, branch, baseBranch, message, CancellationToken.None));
    });

    /// <summary>
    /// Rebases a branch onto another, asking for a message to squash its commits into first when it has several.
    /// </summary>
    /// <param name="branch">The branch to rebase.</param>
    /// <param name="onto">The branch to rebase it onto.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task Rebase(string branch, string onto) => WithIdentity(async () =>
    {
        if (status.HasChanges)
        {
            await Notice("Rebase", "Commit or reset your pending changes first - this needs a clean working tree.");
            return;
        }

        GitBranchState? state = await Compare(branch, onto);

        if (state is null)
        {
            return;
        }

        if (state.Unique == 0 && state.IsBased)
        {
            await Notice("Rebase", $"'{branch}' is already up to date with '{onto}'.");
        }
        else if (state.Unique > 1)
        {
            AskSquash("Rebase", branch, onto, state, "Rebase", message => connection.GitRebase(workspaceId, branch, onto, message, CancellationToken.None));
        }
        else
        {
            Ask(GitPrompt.Confirm, "Rebase", $"Rebase '{branch}' onto '{onto}'? Conflicts are resolved by the AI.", "Rebase", () => connection.GitRebase(workspaceId, branch, onto, null, CancellationToken.None));
        }
    });

    /// <summary>
    /// Merges a branch into another, asking for a message to squash its commits into first when it has several.
    /// </summary>
    /// <param name="source">The branch to merge.</param>
    /// <param name="target">The branch it is merged into.</param>
    /// <returns>A task that completes once the question is showing.</returns>
    public Task Merge(string source, string target) => WithIdentity(async () =>
    {
        if (status.HasChanges)
        {
            await Notice("Merge", "Commit or reset your pending changes first - this needs a clean working tree.");
            return;
        }

        GitBranchState? state = await Compare(source, target);

        if (state is null)
        {
            return;
        }

        if (state.Unique == 0)
        {
            await Notice("Merge", $"'{source}' has no commits that aren't already on '{target}'.");
        }
        else if (state.Unique > 1)
        {
            AskSquash("Merge", source, target, state, "Merge", message => connection.GitMerge(workspaceId, source, target, message, CancellationToken.None));
        }
        else
        {
            Ask(GitPrompt.Confirm, "Merge", $"Merge '{source}' into '{target}'? '{source}' is deleted afterwards.", "Merge", () => connection.GitMerge(workspaceId, source, target, null, CancellationToken.None));
        }
    });

    private void AskSquash(string title, string branch, string baseBranch, GitBranchState state, string confirm, Func<string, Task> run)
    {
        string commits = state.Unique == 1 ? "1 commit" : $"{state.Unique} commits";
        Ask(GitPrompt.Input, $"{title} '{branch}'", $"Message for the one commit replacing the {commits} on '{branch}' that aren't on '{baseBranch}'", confirm, () => run(promptText.Trim()), state.Subject, empty: "Enter a commit message.");
    }

    private async Task<GitBranchState?> Compare(string branch, string baseBranch)
    {
        GitBranchState? state = null;

        if (await Attempt(async () => state = await connection.GetGitBranchState(workspaceId, branch, baseBranch, CancellationToken.None)))
        {
            return state;
        }

        await Notice("Git", Error ?? "Git failed.");
        return null;
    }

    private async Task Fetch()
    {
        await Attempt(() => connection.GitFetch(workspaceId, CancellationToken.None));
        await Refresh();
        await History.Changed();
    }

    /// <summary>
    /// Starts fetching and pulling the checked out branch now and then, which a workspace does while it is focused.
    /// </summary>
    public void StartSync()
    {
        if (syncing is not null)
        {
            return;
        }

        syncing = new CancellationTokenSource();
        _ = SyncLoop(syncing.Token);
    }

    /// <summary>
    /// Stops the periodic fetch and pull.
    /// </summary>
    public void StopSync()
    {
        syncing?.Cancel();
        syncing = null;
    }

    /// <summary>
    /// Fetches the remote and pulls the checked out branch once, when the workspace has a remote and no git work is going on.
    /// </summary>
    /// <returns>A task that completes once the server is done.</returns>
    public async Task SyncOnce()
    {
        if (!status.IsRepo || status.Remote is null || IsWorking)
        {
            return;
        }

        try
        {
            await connection.GitSync(workspaceId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    private async Task SyncLoop(CancellationToken cancellation)
    {
        using PeriodicTimer timer = new(syncInterval);

        try
        {
            do
            {
                await SyncOnce();
            }
            while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Confirm()
    {
        if (prompt == GitPrompt.Notice)
        {
            Prompt = GitPrompt.None;
            return;
        }

        if (prompt == GitPrompt.Input && promptText.Trim().Length == 0)
        {
            Error = emptyMessage;
            return;
        }

        Func<Task>? action = confirmAction;
        Func<Task>? next = afterwards;

        if (prompt == GitPrompt.Identity)
        {
            if (promptText.Trim().Length == 0 || promptEmail.Trim().Length == 0)
            {
                Error = "Enter your name and email.";
                return;
            }

            string name = promptText.Trim();
            string email = promptEmail.Trim();
            action = () => connection.GitSetIdentity(workspaceId, name, email, CancellationToken.None);
        }

        if (action is null)
        {
            Prompt = GitPrompt.None;
            return;
        }

        bool worked = await Attempt(action);

        if (worked)
        {
            Prompt = GitPrompt.None;
            confirmAction = null;
            afterwards = null;
        }

        await Refresh();
        await History.Changed();

        if (worked && next is not null)
        {
            await next();
        }
    }
}
