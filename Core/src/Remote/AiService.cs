namespace Markwardt.AutoRemote;

/// <summary>
/// The AI service a workspace's prompts run on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiService
{
    /// <summary>
    /// Anthropic's Claude Code.
    /// </summary>
    ClaudeCode,

    /// <summary>
    /// OpenAI's Codex.
    /// </summary>
    Codex
}

/// <summary>
/// What each <see cref="AiService" /> offers.
/// </summary>
public static class AiServiceExtensions
{
    extension(AiService service)
    {
        /// <summary>
        /// Gets the name shown to the user.
        /// </summary>
        /// <returns>The display name.</returns>
        public string Label() => service switch
        {
            AiService.Codex => "OpenAI Codex",
            _ => "Claude Code"
        };

        /// <summary>
        /// Gets the models the service offers.
        /// </summary>
        /// <returns>The models, the default first.</returns>
        public IReadOnlyList<AiModel> Models() => service switch
        {
            AiService.Codex => [AiModel.Terra, AiModel.Sol, AiModel.Luna],
            _ => [AiModel.Sonnet, AiModel.Opus, AiModel.Haiku]
        };

        /// <summary>
        /// Gets the efforts the service offers.
        /// </summary>
        /// <returns>The efforts from least to most.</returns>
        public IReadOnlyList<AiEffort> Efforts() => [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.XHigh, AiEffort.Max];

        /// <summary>
        /// Gets the effort the service uses unless told otherwise.
        /// </summary>
        /// <returns>The default effort.</returns>
        public AiEffort DefaultEffort() => service == AiService.Codex ? AiEffort.Medium : AiEffort.High;
    }
}
