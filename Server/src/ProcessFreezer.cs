namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Suspends and continues a process together with everything it started, which pauses an AI mid-work: its commands stop too and nothing is lost.
/// </summary>
internal interface IProcessFreezer
{
    /// <summary>
    /// Suspends a process and all of its descendants.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <returns>A task that completes once they are suspended.</returns>
    /// <exception cref="RequestException">The process could not be suspended.</exception>
    Task Freeze(int processId);

    /// <summary>
    /// Continues a process and all of its descendants that <see cref="Freeze" /> suspended.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <returns>A task that completes once they run again.</returns>
    /// <exception cref="RequestException">The process could not be continued.</exception>
    Task Thaw(int processId);
}

/// <inheritdoc />
internal sealed class ProcessFreezer : IProcessFreezer
{
    private readonly int stop = 19;

    private readonly int resume = 18;

    /// <inheritdoc />
    public async Task Freeze(int processId)
    {
        foreach (int id in await Tree(processId))
        {
            Signal(id, true);
        }
    }

    /// <inheritdoc />
    public async Task Thaw(int processId)
    {
        foreach (int id in (await Tree(processId)).Reverse())
        {
            Signal(id, false);
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")]
    private static extern int SuspendProcess(nint handle);

    [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")]
    private static extern int ResumeProcess(nint handle);

    private void Signal(int processId, bool freeze)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Process process = Process.GetProcessById(processId);
                _ = freeze ? SuspendProcess(process.Handle) : ResumeProcess(process.Handle);
            }
            else if (Kill(processId, freeze ? stop : resume) != 0 && Marshal.GetLastWin32Error() != 3)
            {
                throw new RequestException($"The AI could not be {(freeze ? "paused" : "resumed")}.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            if (exception is ArgumentException or InvalidOperationException)
            {
                return;
            }

            throw new RequestException($"The AI could not be {(freeze ? "paused" : "resumed")} on this machine.");
        }
    }

    private async Task<IReadOnlyList<int>> Tree(int root)
    {
        Dictionary<int, List<int>> children = [];

        foreach (string line in (await Run(OperatingSystem.IsWindows() ? "powershell.exe" : "ps", OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-Command", "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId)\" }"] : ["-A", "-o", "pid=,ppid="])).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 && int.TryParse(parts[0], out int id) && int.TryParse(parts[1], out int parent))
            {
                if (!children.TryGetValue(parent, out List<int>? list))
                {
                    children[parent] = list = [];
                }

                list.Add(id);
            }
        }

        List<int> ordered = [root];

        for (int index = 0; index < ordered.Count; index++)
        {
            if (children.TryGetValue(ordered[index], out List<int>? below))
            {
                ordered.AddRange(below.Where(id => !ordered.Contains(id)));
            }
        }

        return ordered;
    }

    private async Task<string> Run(string command, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return string.Empty;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return await output;
        }
        catch (Win32Exception)
        {
            return string.Empty;
        }
    }
}
