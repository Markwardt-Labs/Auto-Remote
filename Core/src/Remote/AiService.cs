namespace Markwardt.AutoRemote;

/// <summary>
/// The AI provider of a workspace, one for each kind of setup Live AI offers. It is chosen when the workspace is made and never changes.
/// </summary>
[JsonConverter(typeof(AiServiceConverter))]
public enum AiService
{
    /// <summary>
    /// Claude Code.
    /// </summary>
    ClaudeCode,

    /// <summary>
    /// OpenAI Codex.
    /// </summary>
    Codex,

    /// <summary>
    /// Models on this machine or the network, run through OpenCode and a model server such as Ollama.
    /// </summary>
    Local
}

/// <summary>
/// Reads and writes an <see cref="AiService" /> as its name, also reading the name <c>OpenCode</c> that <see cref="AiService.Local" /> had before.
/// </summary>
public sealed class AiServiceConverter : JsonConverter<AiService>
{
    /// <inheritdoc />
    public override AiService Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() switch
        {
            "OpenCode" => AiService.Local,
            { } name when Enum.TryParse(name, true, out AiService service) => service,
            _ => throw new JsonException("Not the name of an AI service.")
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AiService value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// Describes an <see cref="AiService" />.
/// </summary>
public static class AiServiceExtensions
{
    extension(AiService service)
    {
        /// <summary>
        /// Gets the name to show for the service.
        /// </summary>
        /// <returns>The name.</returns>
        public string Label() => service switch
        {
            AiService.Codex => "OpenAI Codex",
            AiService.Local => "Local models",
            _ => "Claude Code"
        };

        /// <summary>
        /// Gets a value indicating whether a session of the service can run on an API key of its own.
        /// </summary>
        /// <returns><see langword="true" /> for Claude Code and Codex.</returns>
        public bool TakesApiKey() => service != AiService.Local;
    }
}
