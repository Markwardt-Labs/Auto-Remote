namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One entry in the list of AI services a workspace can run on.
/// </summary>
/// <param name="Service">The AI service.</param>
/// <param name="Name">The text shown for the choice.</param>
internal sealed record ServiceChoice(AiService Service, string Name);
