namespace Markwardt.AutoRemote;

/// <summary>
/// What one process takes.
/// </summary>
public sealed record ProcessUsage
{
    /// <summary>
    /// Gets the process identifier on the server's computer.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>
    /// Gets the command line of the process, shortened when it is long.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Gets the memory the process takes, in bytes.
    /// </summary>
    public required long Memory { get; init; }

    /// <summary>
    /// Gets the share of the whole computer's processor the process uses, from 0 to 100, or <see langword="null" /> before it was measured twice.
    /// </summary>
    public double? Cpu { get; init; }
}
