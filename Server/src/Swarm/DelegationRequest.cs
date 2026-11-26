namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The input of the tool that delegates tasks.
/// </summary>
/// <param name="Tasks">The tasks, which make one job.</param>
internal sealed record DelegationRequest(IReadOnlyList<DelegationRequestedTask> Tasks);
