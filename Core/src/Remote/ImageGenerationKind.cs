namespace Markwardt.AutoRemote;

/// <summary>
/// The service that makes the images the AI asks for.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageGenerationKind
{
    /// <summary>
    /// None: the AI cannot make images.
    /// </summary>
    None,

    /// <summary>
    /// OpenAI, with an API key.
    /// </summary>
    OpenAi,

    /// <summary>
    /// A Stable Diffusion server on this machine or the network (AUTOMATIC1111 or Forge).
    /// </summary>
    Local
}
