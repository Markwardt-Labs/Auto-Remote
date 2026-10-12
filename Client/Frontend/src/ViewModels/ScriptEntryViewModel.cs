namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One script or task run shown in the script tab, with the output it wrote.
/// </summary>
internal sealed class ScriptEntryViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptEntryViewModel" /> class.
    /// </summary>
    /// <param name="run">The run as the server reported it.</param>
    public ScriptEntryViewModel(ScriptRun run)
    {
        Path = run.Path;
        Name = run.Name;
        IsTask = run.IsTask;
        TaskPath = run.TaskPath;
        state = run.State;
        output = run.Output;
        trimmed = run.Trimmed;
    }

    private readonly int maxOutput = 500_000;

    private readonly int shownOutput = 100_000;

    private ScriptState state;

    private string output;

    private bool trimmed;

    private bool isSelected;

    /// <summary>
    /// Gets the path of the script or task file relative to the workspace folder, which identifies the run.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the file name without its extension.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets a value indicating whether this is a task.
    /// </summary>
    public bool IsTask { get; }

    /// <summary>
    /// Gets the path of the task that runs this script, or <see langword="null" />.
    /// </summary>
    public string? TaskPath { get; }

    /// <summary>
    /// Gets the scripts a task runs.
    /// </summary>
    public ObservableCollection<ScriptEntryViewModel> Children { get; } = [];

    /// <summary>
    /// Gets or sets how far the run has come.
    /// </summary>
    public ScriptState State
    {
        get => state;
        set
        {
            if (Set(ref state, value))
            {
                Raise(nameof(IsRunning));
                Raise(nameof(IsActive));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the run is in progress.
    /// </summary>
    public bool IsRunning => state == ScriptState.Running;

    /// <summary>
    /// Gets a value indicating whether the run is in progress or waiting for its turn in a task.
    /// </summary>
    public bool IsActive => state is ScriptState.Running or ScriptState.Pending;

    /// <summary>
    /// Gets or sets a value indicating whether this script is the one of its task being viewed.
    /// </summary>
    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    /// <summary>
    /// Gets everything the run wrote, the earliest part cut when it grew too long.
    /// </summary>
    public string Output => output;

    /// <summary>
    /// Gets the end of the output, which is what is shown; a long output is cut at the start to keep the view fast.
    /// </summary>
    public string Shown => output.Length > shownOutput ? "... earlier output is not shown (copy gives all of it) ...\n" + output[^shownOutput..] : trimmed ? "... earlier output was cut ...\n" + output : output;

    /// <summary>
    /// Adds what the run wrote.
    /// </summary>
    /// <param name="text">The new text.</param>
    public void Append(string text)
    {
        output += text;

        if (output.Length > maxOutput)
        {
            output = output[^maxOutput..];
            trimmed = true;
        }

        Raise(nameof(Output));
        Raise(nameof(Shown));
    }
}
