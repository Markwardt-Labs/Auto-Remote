namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the AI says when it could not finish its work.
/// </summary>
/// <param name="Reason">Why, in a sentence or two.</param>
internal sealed record UnfinishedInput([property: JsonPropertyName("reason")] string? Reason = null);
