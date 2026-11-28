namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How a stretch of the AI's work ended, as far as the AI service tells.
/// </summary>
/// <param name="Answered">Whether the AI wrote or did anything.</param>
/// <param name="Failed">Whether it ended with an error.</param>
/// <param name="Stopped">Whether it was interrupted.</param>
/// <param name="Error">The last error, or <see langword="null" />.</param>
/// <param name="Reply">The last thing the AI wrote, or <see langword="null" />.</param>
internal sealed record WorkResult(bool Answered, bool Failed, bool Stopped, string? Error = null, string? Reply = null);
