namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ProcessSourceTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly ProcessSource source;

    public ProcessSourceTests()
    {
        Write("proc/meminfo", "MemTotal:       32000 kB\nMemFree:         1000 kB\nMemAvailable:    8000 kB\nSwapTotal:       4000 kB\nSwapFree:        3000 kB\n");
        Write("proc/stat", "cpu  100 10 50 700 40 5 5 0 9 0\ncpu0 50 5 25 350 20 3 2 0 0 0\ncpu1 50 5 25 350 20 2 3 0 0 0\nintr 12\n");
        Process(10, "systemd", "S", 1);
        Process(100, "server", "S", 10);
        Process(200, "a (odd) name", "R", 100, user: 30, system: 12, started: 4567);
        Process(201, "done", "Z", 200);
        Directory.CreateDirectory(temp.Combine("proc/not-a-process"));
        Write("proc/100/cgroup", "0::/user.slice/app.slice/auto-remote.service\n");
        Write("groups/user.slice/app.slice/auto-remote.service/cgroup.procs", "100\n200\n300\n");
        source = new ProcessSource(temp.Combine("proc"), temp.Combine("groups"), 100);
    }

    public void Dispose() => temp.Dispose();

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temp.Combine(path))!);
        File.WriteAllText(temp.Combine(path), content);
    }

    private void Process(int id, string name, string state, int parent, long user = 1, long system = 1, long started = 100)
        => Write($"proc/{id}/stat", $"{id} ({name}) {state} {parent} 1 1 0 -1 4194304 0 0 0 0 {user} {system} 7 8 20 0 1 0 {started} 1000 200\n");

    [Fact]
    public void List_ReadsTheParentTheStartAndTheProcessorTimeOfEveryProcessThatHasNotEnded()
    {
        IReadOnlyList<ProcessEntry> processes = source.List();

        Assert.Equal([10, 100, 200], processes.Select(process => process.Id).Order().ToList());
        Assert.Equal(new ProcessEntry { Id = 200, ParentId = 100, Started = 4567, Ticks = 42 }, processes.Single(process => process.Id == 200));
    }

    [Fact]
    public void ServiceMembers_ListsTheControlGroupOfAServerThatSystemdStartedAsAService()
        => Assert.Equal([100, 200, 300], source.ServiceMembers().Order().ToList());

    [Fact]
    public void ServiceMembers_ListsNothingWhenSomethingElseStartedTheServerSinceTheGroupThenHoldsOthersProcesses()
    {
        Process(10, "bash", "S", 1);

        Assert.Empty(source.ServiceMembers());
    }

    [Fact]
    public void ServiceMembers_ListsNothingOutsideAService()
    {
        Write("proc/100/cgroup", "0::/user.slice/app.slice/run-123.scope\n");

        Assert.Empty(source.ServiceMembers());
    }

    [Fact]
    public void Memory_TakesTheShareOfSharedMemoryAndOtherwiseWhatIsResident()
    {
        Write("proc/200/smaps_rollup", "00400000-7ffd rollup\nRss:  900 kB\nPss:  600 kB\n");
        Write("proc/200/status", "Name:\ta\nVmRSS:\t  900 kB\n");
        Write("proc/100/status", "Name:\tserver\nVmRSS:\t  50 kB\n");

        Assert.Equal(600 * 1024, source.Memory(200));
        Assert.Equal(50 * 1024, source.Memory(100));
        Assert.Equal(0, source.Memory(999));
    }

    [Fact]
    public void Arguments_SplitsTheCommandLine()
    {
        Write("proc/200/cmdline", "/bin/sh\0-c\0make all\0");

        Assert.Equal(["/bin/sh", "-c", "make all"], source.Arguments(200));
        Assert.Empty(source.Arguments(999));
        Assert.Null(source.Folder(999));
    }

    [Fact]
    public void Machine_ReadsTheMemoryTheCoresAndTheProcessorTime()
    {
        MachineSample machine = source.Machine()!;

        Assert.Equal((32000L * 1024, 8000L * 1024, 4000L * 1024, 3000L * 1024), (machine.TotalMemory, machine.AvailableMemory, machine.TotalSwap, machine.FreeSwap));
        // The time of guests is already part of the time of users, and time spent waiting for a disk is idle time.
        Assert.Equal((2, 910L, 170L), (machine.Cores, machine.TotalTicks, machine.BusyTicks));
    }

    [Fact]
    public async Task ReadsTheRealProcessesOfThisComputer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ProcessSource real = new();
        ProcessStartInfo start = new("/bin/sh", ["-c", "sleep 30; true"]) { WorkingDirectory = temp.Path };
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;

        try
        {
            ProcessEntry found = real.List().Single(process => process.Id == child.Id);

            for (int attempt = 0; attempt < 200 && real.Arguments(child.Id) is not ["/bin/sh", "-c", _]; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(real.IsSupported);
            Assert.Equal(Environment.ProcessId, found.ParentId);
            Assert.Equal(["/bin/sh", "-c", "sleep 30; true"], real.Arguments(child.Id));
            Assert.Equal(new DirectoryInfo(temp.Path).FullName, real.Folder(child.Id));
            Assert.True(real.Memory(child.Id) > 0);
            Assert.True(real.Machine()!.TotalMemory > real.Memory(child.Id));
        }
        finally
        {
            child.Kill(true);
            await child.WaitForExitAsync();
        }
    }
}
