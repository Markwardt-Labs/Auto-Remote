namespace Markwardt.AutoRemote;

/// <summary>
/// One run of a single-file <c>.cs</c> script or a <c>.task</c> file on the server, kept until a client removes it.
/// </summary>
public sealed record ScriptRun
{
    /// <summary>
    /// Gets the path of the script or task file relative to the workspace folder, which identifies the run.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the file name without its extension.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets how far the run has come.
    /// </summary>
    public required ScriptState State { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file is a <c>.task</c> file, which runs the scripts it lists.
    /// </summary>
    public bool IsTask { get; init; }

    /// <summary>
    /// Gets the path of the task this script is run by, or <see langword="null" /> for a run of its own.
    /// </summary>
    public string? TaskPath { get; init; }

    /// <summary>
    /// Gets the paths of the scripts a task runs, in order.
    /// </summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];

    /// <summary>
    /// Gets everything the run wrote so far, with the earliest part cut when it grew too long; empty in a pushed <see cref="RemoteEvent" />, which carries output separately.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the earliest part of <see cref="Output" /> was cut.
    /// </summary>
    public bool Trimmed { get; init; }
}
