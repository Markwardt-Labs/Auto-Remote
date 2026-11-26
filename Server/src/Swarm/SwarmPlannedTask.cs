namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A task to delegate that may build on earlier tasks of the same request.
/// </summary>
/// <param name="Request">The task.</param>
/// <param name="After">The positions, in the same request, of the earlier tasks it builds on.</param>
internal sealed record SwarmPlannedTask(SwarmTaskRequest Request, IReadOnlyList<int> After);
