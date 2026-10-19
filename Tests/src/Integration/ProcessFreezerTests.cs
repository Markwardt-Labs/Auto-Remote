namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ProcessFreezerTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly ProcessFreezer freezer = new();

    private Process? process;

    public void Dispose()
    {
        try
        {
            process?.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }

        process?.Dispose();
        temp.Dispose();
    }

    private int Ticks => File.Exists(temp.Combine("tick")) && int.TryParse(File.ReadAllText(temp.Combine("tick")).Trim(), out int value) ? value : 0;

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(30);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Freeze_StopsAProcessAndWhatItStartedAndThawLetsItCarryOn()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo start = new("/bin/sh") { WorkingDirectory = temp.Path, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("(i=0; while true; do i=$((i+1)); echo $i > tick; sleep 0.05; done)");
        process = Process.Start(start)!;
        await Until(() => Ticks > 2);

        await freezer.Freeze(process.Id);
        await Task.Delay(150);
        int frozen = Ticks;
        await Task.Delay(400);

        Assert.Equal(frozen, Ticks);

        await freezer.Thaw(process.Id);
        await Until(() => Ticks > frozen + 2);
    }

    [Fact]
    public async Task Freeze_OfAProcessThatIsGoneDoesNothing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo start = new("/bin/sh") { RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exit 0");
        process = Process.Start(start)!;
        await process.WaitForExitAsync();

        await freezer.Freeze(process.Id);
        await freezer.Thaw(process.Id);
    }
}
