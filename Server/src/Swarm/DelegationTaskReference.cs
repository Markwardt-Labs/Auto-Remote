namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The input of a tool that works on one delegated task.
/// </summary>
/// <param name="TaskId">The identifier of the task.</param>
/// <param name="File">The only file whose changes to show, when reviewing.</param>
internal sealed record DelegationTaskReference(string TaskId, string? File = null);
