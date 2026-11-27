namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The memory of the computer and the processor time it has counted so far.
/// </summary>
internal sealed record MachineSample
{
    /// <summary>
    /// Gets the memory the computer has, in bytes.
    /// </summary>
    public required long TotalMemory { get; init; }

    /// <summary>
    /// Gets the memory that can still be taken without swapping, in bytes.
    /// </summary>
    public required long AvailableMemory { get; init; }

    /// <summary>
    /// Gets the swap space, in bytes.
    /// </summary>
    public long TotalSwap { get; init; }

    /// <summary>
    /// Gets the free swap space, in bytes.
    /// </summary>
    public long FreeSwap { get; init; }

    /// <summary>
    /// Gets how many processor cores there are.
    /// </summary>
    public required int Cores { get; init; }

    /// <summary>
    /// Gets the processor time of all cores together since the computer started, in clock ticks.
    /// </summary>
    public required long TotalTicks { get; init; }

    /// <summary>
    /// Gets the part of <see cref="TotalTicks" /> the cores were not idle.
    /// </summary>
    public required long BusyTicks { get; init; }
}
