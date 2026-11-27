namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Reads the processes of the computer.
/// </summary>
internal interface IProcessSource
{
    /// <summary>
    /// Gets whether processes can be read on this computer.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Gets the identifier of the server's own process.
    /// </summary>
    int ServerId { get; }

    /// <summary>
    /// Lists the running processes, without those that have ended and wait to be collected.
    /// </summary>
    /// <returns>The processes.</returns>
    IReadOnlyList<ProcessEntry> List();

    /// <summary>
    /// Lists the processes of the service the server runs as, which holds whatever the server started even after it detached from the server.
    /// </summary>
    /// <returns>The process identifiers, none unless the server was started as a systemd service of its own.</returns>
    IReadOnlySet<int> ServiceMembers();

    /// <summary>
    /// Reads the command line of a process.
    /// </summary>
    /// <param name="id">The process.</param>
    /// <returns>The program and its arguments, none when they cannot be read.</returns>
    IReadOnlyList<string> Arguments(int id);

    /// <summary>
    /// Reads the working folder of a process.
    /// </summary>
    /// <param name="id">The process.</param>
    /// <returns>The folder, or <see langword="null" /> when it cannot be read.</returns>
    string? Folder(int id);

    /// <summary>
    /// Reads the memory a process takes, with the memory it shares divided between those that share it where the system allows reading that.
    /// </summary>
    /// <param name="id">The process.</param>
    /// <returns>The memory in bytes, zero when it cannot be read.</returns>
    long Memory(int id);

    /// <summary>
    /// Reads the memory and processor time of the whole computer.
    /// </summary>
    /// <returns>The reading, or <see langword="null" /> when it cannot be made.</returns>
    MachineSample? Machine();
}

/// <summary>
/// Reads processes from the process file system of Linux.
/// </summary>
/// <param name="root">Where the process file system is.</param>
/// <param name="groups">Where the control group file system is.</param>
/// <param name="serverId">The server's own process, this process when left out.</param>
internal sealed class ProcessSource(string root = "/proc", string groups = "/sys/fs/cgroup", int? serverId = null) : IProcessSource
{
    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsLinux() && File.Exists(Path.Combine(root, "meminfo"));

    /// <inheritdoc />
    public int ServerId { get; } = serverId ?? Environment.ProcessId;

    /// <inheritdoc />
    public IReadOnlyList<ProcessEntry> List()
    {
        List<ProcessEntry> processes = [];

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int id) && Stat(id) is { State: not ("Z" or "X") } stat)
            {
                processes.Add(new ProcessEntry { Id = id, ParentId = stat.ParentId, Started = stat.Started, Ticks = stat.Ticks });
            }
        }

        return processes;
    }

    /// <inheritdoc />
    public IReadOnlySet<int> ServiceMembers()
    {
        HashSet<int> members = [];

        if (Stat(ServerId) is not { } server || Stat(server.ParentId)?.Name != "systemd")
        {
            return members;
        }

        string? group = Text(Path.Combine(root, Number(ServerId), "cgroup"))?.Split('\n').FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..].Trim();

        if (group is null || !group.EndsWith(".service", StringComparison.Ordinal))
        {
            return members;
        }

        foreach (string line in Text(Path.Combine(groups, group.TrimStart('/'), "cgroup.procs"))?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [])
        {
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                members.Add(id);
            }
        }

        return members;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments(int id)
        => Text(Path.Combine(root, Number(id), "cmdline"))?.TrimEnd('\0') is { Length: > 0 } line ? line.Split('\0') : [];

    /// <inheritdoc />
    public string? Folder(int id)
    {
        try
        {
            return new DirectoryInfo(Path.Combine(root, Number(id), "cwd")).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public long Memory(int id)
        => Kilobytes(Text(Path.Combine(root, Number(id), "smaps_rollup")), "Pss:") ?? Kilobytes(Text(Path.Combine(root, Number(id), "status")), "VmRSS:") ?? 0;

    /// <inheritdoc />
    public MachineSample? Machine()
    {
        string? memory = Text(Path.Combine(root, "meminfo"));
        string[] lines = Text(Path.Combine(root, "stat"))?.Split('\n') ?? [];
        long[] times = lines.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(Parse).ToArray() ?? [];

        if (Kilobytes(memory, "MemTotal:") is not { } total || Kilobytes(memory, "MemAvailable:") is not { } available || times.Length < 5)
        {
            return null;
        }

        return new MachineSample
        {
            TotalMemory = total,
            AvailableMemory = available,
            TotalSwap = Kilobytes(memory, "SwapTotal:") ?? 0,
            FreeSwap = Kilobytes(memory, "SwapFree:") ?? 0,
            Cores = Math.Max(1, lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(line[3]))),
            TotalTicks = times.Sum(),
            BusyTicks = times.Sum() - times[3] - times[4],
        };
    }

    private (string Name, string State, int ParentId, long Ticks, long Started)? Stat(int id)
    {
        string? text = Text(Path.Combine(root, Number(id), "stat"));

        if (text is null)
        {
            return null;
        }

        // The name is in parentheses and may itself hold spaces and parentheses, so the fields are counted from the last one.
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');

        if (open < 0 || close < open)
        {
            return null;
        }

        string[] fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length < 20 ? null : (text[(open + 1)..close], fields[0], (int)Parse(fields[1]), Parse(fields[11]) + Parse(fields[12]), Parse(fields[19]));
    }

    private long? Kilobytes(string? text, string name)
        => text?.Split('\n').FirstOrDefault(line => line.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is [var value, ..] ? Parse(value) * 1024 : null;

    private long Parse(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

    private string Number(int id) => id.ToString(CultureInfo.InvariantCulture);

    private string? Text(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
