namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The instructions every workspace's AI always gets, in front of the ones the user sets, which the user never sees.
/// </summary>
internal interface IBuiltInInstructions
{
    /// <summary>
    /// Gets the built-in instructions.
    /// </summary>
    string Text { get; }

    /// <summary>
    /// Puts the built-in instructions in front of the user's own, which is what the AI session is configured with.
    /// </summary>
    /// <param name="user">The user's own instructions, or <see langword="null" /> or blank for none; the built-in ones in front of them are not doubled.</param>
    /// <returns>The instructions for the session.</returns>
    string Compose(string? user);

    /// <summary>
    /// Takes the built-in instructions off what a session is configured with, leaving what the user wrote.
    /// </summary>
    /// <param name="stored">The instructions of the session.</param>
    /// <returns>The user's own instructions, trimmed, or <see langword="null" /> when there are none.</returns>
    string? Visible(string? stored);
}

/// <inheritdoc />
internal sealed class BuiltInInstructions : IBuiltInInstructions
{
    /// <inheritdoc />
    public string Text { get; } = "Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, write a short status update as a normal message, saying what you have done and what comes next, at least once every five minutes so the user can follow along; split the work so that no single step runs much longer than five minutes.";

    /// <inheritdoc />
    public string Compose(string? user) => Visible(user) is { } own ? $"{Text}\n\n{own}" : Text;

    /// <inheritdoc />
    public string? Visible(string? stored)
    {
        string text = stored?.Trim() ?? string.Empty;

        if (text.StartsWith(Text, StringComparison.Ordinal))
        {
            text = text[Text.Length..].Trim();
        }

        return text.Length == 0 ? null : text;
    }
}
