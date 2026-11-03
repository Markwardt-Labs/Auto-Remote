namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One entry in the list of models an AI service offers.
/// </summary>
/// <param name="Id">The identifier of the model.</param>
/// <param name="Name">The text shown for the choice.</param>
internal sealed record ModelChoice(string Id, string Name);
