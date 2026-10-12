namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One entry in the list of places a workspace or group can be moved to.
/// </summary>
/// <param name="Id">The group identifier, or <see langword="null" /> for the top level.</param>
/// <param name="Name">The text shown for the choice: the group's path from the top level.</param>
internal sealed record GroupChoice(Guid? Id, string Name);
