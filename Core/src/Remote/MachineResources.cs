namespace Markwardt.AutoRemote;

/// <summary>
/// The memory and processor of the server's computer.
/// </summary>
public sealed record MachineResources
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
    /// Gets the swap space the computer has, in bytes, zero when it has none.
    /// </summary>
    public long TotalSwap { get; init; }

    /// <summary>
    /// Gets the swap space that is free, in bytes.
    /// </summary>
    public long FreeSwap { get; init; }

    /// <summary>
    /// Gets how many processor cores the computer has.
    /// </summary>
    public int Cores { get; init; }

    /// <summary>
    /// Gets how busy all cores together are, from 0 to 100, or <see langword="null" /> before it was measured twice.
    /// </summary>
    public double? Cpu { get; init; }
}
