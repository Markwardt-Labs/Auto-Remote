namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A task as the AI asks to delegate it.
/// </summary>
/// <param name="Title">A short title.</param>
/// <param name="Instructions">What to do, complete on its own.</param>
/// <param name="Capability">Fast, Balanced, Strong or Max; Balanced when left out.</param>
/// <param name="ExpectedResult">What the result is expected to be and how to check it.</param>
/// <param name="Worker">The name or identifier of the worker, or left out for any networked one.</param>
/// <param name="AfterTasksInThisCall">The positions, from 1, of earlier tasks of the same call this one builds on.</param>
/// <param name="AfterTaskIds">The identifiers of earlier tasks this one builds on.</param>
internal sealed record DelegationRequestedTask(string Title, string Instructions, string? Capability = null, string? ExpectedResult = null, string? Worker = null, IReadOnlyList<int>? AfterTasksInThisCall = null, IReadOnlyList<string>? AfterTaskIds = null);
