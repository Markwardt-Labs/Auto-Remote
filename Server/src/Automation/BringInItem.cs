namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Work that waits to be brought into a workspace by its AI.
/// </summary>
/// <param name="Id">The side session or delegated task.</param>
/// <param name="Title">What a list says about it.</param>
/// <param name="Commit">Whether the changes are committed after it.</param>
/// <param name="Waiting">Why it cannot have its turn yet, or <see langword="null" />.</param>
internal sealed record BringInItem(Guid Id, string Title, bool Commit, string? Waiting = null);
