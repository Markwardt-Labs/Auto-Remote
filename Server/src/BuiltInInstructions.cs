namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The instructions every workspace's AI always gets, in front of the ones the user sets, which the user never sees.
/// </summary>
internal interface IBuiltInInstructions
{
    /// <summary>
    /// Gets the name of the tool the AI reports its status with.
    /// </summary>
    string StatusTool { get; }

    /// <summary>
    /// Writes the built-in instructions for a status interval.
    /// </summary>
    /// <param name="statusMinutes">How often, in minutes, the AI must report its status.</param>
    /// <returns>The instructions.</returns>
    string Text(int statusMinutes);

    /// <summary>
    /// Puts the built-in instructions in front of the user's own, which is what the AI session is configured with.
    /// </summary>
    /// <param name="user">The user's own instructions, or <see langword="null" /> or blank for none; built-in instructions already in front of them are replaced, not doubled.</param>
    /// <param name="statusMinutes">How often, in minutes, the AI must report its status.</param>
    /// <returns>The instructions for the session.</returns>
    string Compose(string? user, int statusMinutes);

    /// <summary>
    /// Takes the built-in instructions off what a session is configured with, whichever interval or older wording they have, leaving what the user wrote.
    /// </summary>
    /// <param name="stored">The instructions of the session.</param>
    /// <returns>The user's own instructions, trimmed, or <see langword="null" /> when there are none.</returns>
    string? Visible(string? stored);
}

/// <inheritdoc />
internal sealed class BuiltInInstructions : IBuiltInInstructions
{
    private readonly string open = "<auto-remote-instructions>\n";

    private readonly string close = "\n</auto-remote-instructions>";

    private readonly string earlier = "Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, write a short status update as a normal message, saying what you have done and what comes next, at least once every five minutes so the user can follow along; split the work so that no single step runs much longer than five minutes.";

    /// <inheritdoc />
    public string StatusTool { get; } = "report_status";

    /// <inheritdoc />
    public string Text(int statusMinutes)
    {
        string span = statusMinutes == 1 ? "minute" : $"{statusMinutes} minutes";
        return $"Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, report your status with the {StatusTool} tool, saying what you have done and what comes next, at least once every {span} so the user can follow along, and split the work so that no single step runs much longer than that. Give status updates only through the {StatusTool} tool and never as a normal message. Your only normal reply is the final summary of what you completed, written once all of the work is finished.";
    }

    /// <inheritdoc />
    public string Compose(string? user, int statusMinutes) => Visible(user) is { } own ? $"{open}{Text(statusMinutes)}{close}\n\n{own}" : $"{open}{Text(statusMinutes)}{close}";

    /// <inheritdoc />
    public string? Visible(string? stored)
    {
        string text = stored?.Trim() ?? string.Empty;

        if (text.StartsWith(open, StringComparison.Ordinal) && text.IndexOf(close, StringComparison.Ordinal) is int end and >= 0)
        {
            text = text[(end + close.Length)..].Trim();
        }
        else if (text.StartsWith(earlier, StringComparison.Ordinal))
        {
            text = text[earlier.Length..].Trim();
        }

        return text.Length == 0 ? null : text;
    }
}
