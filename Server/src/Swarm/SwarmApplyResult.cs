namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What applying the changes of a delegated task to a workspace did.
/// </summary>
/// <param name="Conflicts">The files left with conflict markers to resolve.</param>
/// <param name="Files">How many files the changes touch.</param>
internal sealed record SwarmApplyResult(IReadOnlyList<string> Conflicts, int Files);
