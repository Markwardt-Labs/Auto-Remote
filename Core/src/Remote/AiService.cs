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
    Codex,

    /// <summary>
    /// OpenCode, which runs the models of servers on the server's machine (Ollama or another OpenAI compatible server) and those of its own configuration.
    /// </summary>
    OpenCode
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
            AiService.OpenCode => "OpenCode",
            _ => "Claude Code"
        };

        /// <summary>
        /// Gets the model a workspace starts with on the service when the service offers it.
        /// </summary>
        /// <returns>The identifier of the model, or <see langword="null" /> when the first model the service lists is used.</returns>
        public string? DefaultModel() => service switch
        {
            AiService.ClaudeCode => "sonnet",
            AiService.Codex => "gpt-5.6-terra",
            _ => null
        };

        /// <summary>
        /// Gets the values of model settings a new session on the service starts with in place of the model's own defaults, used for the settings the model has and takes.
        /// </summary>
        /// <returns>The values by setting identifier.</returns>
        public IReadOnlyDictionary<string, string> DefaultSettings() => service == AiService.ClaudeCode ? new Dictionary<string, string> { ["effort"] = "high" } : new Dictionary<string, string>();
    }
}
