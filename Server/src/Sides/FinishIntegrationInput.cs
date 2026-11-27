namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The input of the tool that finishes an integration.
/// </summary>
/// <param name="IntegrationId">The identifier the request to integrate gave.</param>
internal sealed record FinishIntegrationInput([property: JsonPropertyName("integration_id")] string? IntegrationId = null);
