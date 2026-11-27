namespace Markwardt.AutoRemote;

/// <summary>
/// What a number of processes take together.
/// </summary>
public sealed record ResourceUsage
{
    /// <summary>
    /// Gets the memory the processes take, in bytes. Memory they share is divided between them, so it is not counted twice.
    /// </summary>
    public long Memory { get; init; }

    /// <summary>
    /// Gets the share of the whole computer's processor the processes use, from 0 to 100, or <see langword="null" /> before it was measured twice.
    /// </summary>
    public double? Cpu { get; init; }

    /// <summary>
    /// Gets how many processes there are.
    /// </summary>
    public int Processes { get; init; }

    /// <summary>
    /// Gets the processes that take the most memory and those that keep the processor busy, the one taking the most memory first.
    /// </summary>
    public IReadOnlyList<ProcessUsage> Heaviest { get; init; } = [];
}
