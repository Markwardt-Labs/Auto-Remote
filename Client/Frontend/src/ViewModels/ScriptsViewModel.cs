namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The script and task runs of one workspace, which the server runs and keeps; follows them live and lets one be viewed, fed input and stopped.
/// </summary>
internal sealed class ScriptsViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace whose runs are shown.</param>
    public ScriptsViewModel(IServerConnection connection, Guid workspaceId)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        SendInputCommand = new AsyncCommand(_ => SendInput(), () => Displayed is { IsRunning: true, IsTask: false } && !string.IsNullOrEmpty(inputText));
        StopCommand = new AsyncCommand(_ => Stop(), () => selected?.IsActive == true);
        RemoveCommand = new AsyncCommand(entry => Remove(entry as ScriptEntryViewModel));
        SelectChildCommand = new AsyncCommand(entry =>
        {
            Select(entry as ScriptEntryViewModel);
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly List<RemoteEvent> early = [];

    private ScriptEntryViewModel? selected;

    private ScriptEntryViewModel? child;

    private string inputText = string.Empty;

    private string? wanted;

    private bool isRunning;

    private bool loaded;

    private bool subscribed;

    /// <summary>
    /// Raised when the running state of the workspace's scripts changed.
    /// </summary>
    public event Action? RunningChanged;

    /// <summary>
    /// Gets the runs held, tasks and scripts that ran on their own, in the order they started.
    /// </summary>
    public ObservableCollection<ScriptEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Gets a value indicating whether a script or task is running, which keeps the AI and other scripts from starting.
    /// </summary>
    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (Set(ref isRunning, value))
            {
                RunningChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether any run is held.
    /// </summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Gets or sets the run being viewed; a task shows one of its scripts.
    /// </summary>
    public ScriptEntryViewModel? SelectedEntry
    {
        get => selected;
        set
        {
            if (Set(ref selected, value))
            {
                child = value is { IsTask: true } ? value.Children.FirstOrDefault(item => item.IsRunning) ?? value.Children.FirstOrDefault() : null;
                MarkChild();
                RaiseView();
            }
        }
    }

    /// <summary>
    /// Gets the scripts of the viewed task, which are shown as sub tabs.
    /// </summary>
    public ObservableCollection<ScriptEntryViewModel>? Children => selected?.Children;

    /// <summary>
    /// Gets a value indicating whether the viewed run is a task.
    /// </summary>
    public bool IsTaskSelected => selected?.IsTask == true;

    /// <summary>
    /// Gets the script whose output is shown: the viewed script, or the viewed script of the viewed task.
    /// </summary>
    public ScriptEntryViewModel? Displayed => selected is { IsTask: true } ? child : selected;

    /// <summary>
    /// Gets a value indicating whether a script's output is shown.
    /// </summary>
    public bool HasScript => Displayed is not null;

    /// <summary>
    /// Gets the name of the script whose output is shown.
    /// </summary>
    public string ScriptName => Displayed?.Name ?? string.Empty;

    /// <summary>
    /// Gets the output shown.
    /// </summary>
    public string OutputText => Displayed?.Shown ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether the script shown is running.
    /// </summary>
    public bool ShowRunning => Displayed?.IsRunning == true;

    /// <summary>
    /// Gets a value indicating whether the script shown succeeded.
    /// </summary>
    public bool ShowSucceeded => Displayed?.State == ScriptState.Succeeded;

    /// <summary>
    /// Gets a value indicating whether the script shown was stopped.
    /// </summary>
    public bool ShowStopped => Displayed?.State == ScriptState.Stopped;

    /// <summary>
    /// Gets a value indicating whether the script shown failed.
    /// </summary>
    public bool ShowFailed => Displayed?.State == ScriptState.Failed;

    /// <summary>
    /// Gets a value indicating whether the script shown never ran because its task stopped before its turn.
    /// </summary>
    public bool ShowSkipped => Displayed?.State == ScriptState.Skipped;

    /// <summary>
    /// Gets a value indicating whether the viewed run is in progress and can be stopped, a whole task too.
    /// </summary>
    public bool ShowStop => selected?.IsActive == true;

    /// <summary>
    /// Gets the paths of the scripts and tasks that are running or waiting for their turn.
    /// </summary>
    public IReadOnlySet<string> ActivePaths => Entries.SelectMany(entry => entry.Children.Prepend(entry)).Where(entry => entry.IsActive).Select(entry => entry.Path).ToHashSet();

    /// <summary>
    /// Gets or sets the line typed for the running script.
    /// </summary>
    public string InputText
    {
        get => inputText;
        set
        {
            if (Set(ref inputText, value))
            {
                SendInputCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets the command that sends <see cref="InputText" /> to the running script.
    /// </summary>
    public AsyncCommand SendInputCommand { get; }

    /// <summary>
    /// Gets the command that stops the viewed run.
    /// </summary>
    public AsyncCommand StopCommand { get; }

    /// <summary>
    /// Gets the command that forgets a finished run, given as the parameter.
    /// </summary>
    public AsyncCommand RemoveCommand { get; }

    /// <summary>
    /// Gets the command that shows another script of the viewed task, given as the parameter.
    /// </summary>
    public AsyncCommand SelectChildCommand { get; }

    /// <summary>
    /// Loads the runs the server holds and starts following them live.
    /// </summary>
    /// <returns>A task that completes once the runs are shown.</returns>
    public async Task Start()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
        }

        ScriptRuns? runs = null;

        if (await Attempt(async () => runs = await connection.GetScripts(workspaceId, CancellationToken.None)))
        {
            string? viewing = selected?.Path;
            Entries.Clear();

            foreach (ScriptRun run in runs!.Runs)
            {
                Add(run);
            }

            SelectedEntry = viewing is null ? null : Entries.FirstOrDefault(entry => entry.Path == viewing);
            Refresh();
        }

        loaded = true;

        foreach (RemoteEvent remoteEvent in early)
        {
            Apply(remoteEvent);
        }

        early.Clear();
    }

    /// <summary>
    /// Runs a script or task and shows it.
    /// </summary>
    /// <param name="path">The file, relative to the workspace folder.</param>
    /// <returns><see langword="true" /> if the server started the run.</returns>
    public async Task<bool> Run(string path)
    {
        wanted = path;

        if (await Attempt(() => connection.RunScript(workspaceId, path, CancellationToken.None)))
        {
            return true;
        }

        wanted = null;
        return false;
    }

    /// <summary>
    /// Shows why a script could not be run.
    /// </summary>
    /// <param name="message">The reason.</param>
    public void Reject(string message) => Error = message;

    /// <summary>
    /// Stops following the runs.
    /// </summary>
    public void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
        }
    }

    private async Task Stop()
    {
        if (selected is { } entry)
        {
            await Attempt(() => connection.StopScript(workspaceId, entry.Path, CancellationToken.None));
        }
    }

    private async Task SendInput()
    {
        if (Displayed is { } entry)
        {
            string text = inputText;
            InputText = string.Empty;

            if (!await Attempt(() => connection.SendScriptInput(workspaceId, entry.Path, text, CancellationToken.None)) && inputText.Length == 0)
            {
                InputText = text;
            }
        }
    }

    private async Task Remove(ScriptEntryViewModel? entry)
    {
        if (entry is not null)
        {
            await Attempt(() => connection.RemoveScript(workspaceId, entry.Path, CancellationToken.None));
        }
    }

    private void Select(ScriptEntryViewModel? entry)
    {
        if (entry is not null && selected?.Children.Contains(entry) == true)
        {
            child = entry;
            MarkChild();
            RaiseView();
        }
    }

    private void MarkChild()
    {
        foreach (ScriptEntryViewModel item in selected?.Children ?? [])
        {
            item.IsSelected = item == child;
        }
    }

    private ScriptEntryViewModel? Find(string path) => Entries.FirstOrDefault(entry => entry.Path == path) ?? Entries.SelectMany(entry => entry.Children).FirstOrDefault(entry => entry.Path == path);

    private void Add(ScriptRun run)
    {
        ScriptEntryViewModel entry = new(run);

        if (run.TaskPath is { } task)
        {
            ScriptEntryViewModel? parent = Entries.FirstOrDefault(item => item.Path == task);
            parent?.Children.Add(entry);

            if (parent is not null && parent == selected && child is null)
            {
                child = entry;
                MarkChild();
            }
        }
        else
        {
            Entries.Add(entry);
        }
    }

    private void Refresh()
    {
        IsRunning = Entries.Any(entry => entry.IsActive);
        Raise(nameof(ActivePaths));
        Raise(nameof(HasEntries));
        RaiseView();
    }

    private void RaiseView()
    {
        Raise(nameof(Children));
        Raise(nameof(IsTaskSelected));
        Raise(nameof(Displayed));
        Raise(nameof(HasScript));
        Raise(nameof(ScriptName));
        Raise(nameof(OutputText));
        Raise(nameof(ShowRunning));
        Raise(nameof(ShowSucceeded));
        Raise(nameof(ShowStopped));
        Raise(nameof(ShowFailed));
        Raise(nameof(ShowSkipped));
        Raise(nameof(ShowStop));
        SendInputCommand.Refresh();
        StopCommand.Refresh();
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId != workspaceId || remoteEvent.Kind is not (RemoteEventKind.ScriptChanged or RemoteEventKind.ScriptOutput or RemoteEventKind.ScriptRemoved))
        {
            return;
        }

        if (loaded)
        {
            Apply(remoteEvent);
        }
        else
        {
            early.Add(remoteEvent);
        }
    }

    private void Apply(RemoteEvent remoteEvent)
    {
        switch (remoteEvent.Kind)
        {
            case RemoteEventKind.ScriptChanged when remoteEvent.Script is { } run:
                if (Find(run.Path) is { } existing)
                {
                    existing.State = run.State;

                    if (run.State == ScriptState.Running && run.TaskPath is not null && selected?.Children.Contains(existing) == true)
                    {
                        child = existing;
                        MarkChild();
                    }
                }
                else
                {
                    Add(run);

                    if (run.Path == wanted && Find(run.Path) is { TaskPath: null } added)
                    {
                        wanted = null;
                        SelectedEntry = added;
                    }
                }

                Refresh();
                break;
            case RemoteEventKind.ScriptOutput when remoteEvent.Path is { } path && remoteEvent.Text is { } text:
                if (Find(path) is { } target)
                {
                    target.Append(text);

                    if (target == Displayed)
                    {
                        Raise(nameof(OutputText));
                    }
                }

                break;
            case RemoteEventKind.ScriptRemoved when remoteEvent.Path is { } removed:
                RemoveEntry(removed);
                Refresh();
                break;
        }
    }

    private void RemoveEntry(string path)
    {
        if (Entries.FirstOrDefault(entry => entry.Path == path) is { } top)
        {
            Entries.Remove(top);

            if (selected == top)
            {
                SelectedEntry = null;
            }

            return;
        }

        foreach (ScriptEntryViewModel entry in Entries)
        {
            if (entry.Children.FirstOrDefault(item => item.Path == path) is { } found)
            {
                entry.Children.Remove(found);

                if (child == found)
                {
                    child = entry.Children.FirstOrDefault();
                    MarkChild();
                }
            }
        }
    }
}
