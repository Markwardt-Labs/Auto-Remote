namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Starts conversations with the AI service of a workspace.
/// </summary>
internal interface IAiRunner
{
    /// <summary>
    /// Starts a conversation that takes prompts until it is disposed.
    /// </summary>
    /// <param name="config">The server settings naming the executables and arguments.</param>
    /// <param name="service">The AI service to run on.</param>
    /// <param name="folder">The working folder, which is also what scopes the service's conversation.</param>
    /// <param name="model">The model to run on.</param>
    /// <param name="effort">The effort to run with.</param>
    /// <param name="aiSession">The identifier of the AI session to run in.</param>
    /// <param name="resume">Whether <paramref name="aiSession" /> already exists and is resumed, instead of being started; a service that chooses identifiers itself reports the new one as <see cref="AiOutputKind.Session" />.</param>
    /// <returns>The running conversation.</returns>
    /// <exception cref="RequestException">The service could not be started.</exception>
    IAiConversation Start(ServerConfig config, AiService service, string folder, AiModel model, AiEffort effort, Guid aiSession, bool resume);
}

/// <inheritdoc />
internal sealed class AiRunner(IProcessFreezer freezer) : IAiRunner
{
    private readonly string[] claudeDisabledTools = ["Agent", "Task", "Workflow", "Monitor", "ScheduleWakeup", "CronCreate", "CronDelete", "CronList", "RemoteTrigger", "SendMessage", "ListAgents", "TaskStop"];

    private readonly string workInstruction = "Do all of the work yourself, in the foreground. Never use subagents, background tasks, background commands, scheduled or recurring work. Run every command to completion and wait for it, and only reply once all of the work is finished. While you work, write a short status update as a normal message, saying what you have done and what comes next, at least once every five minutes so the user can follow along; split the work so that no single step runs much longer than five minutes.";

    /// <inheritdoc />
    public IAiConversation Start(ServerConfig config, AiService service, string folder, AiModel model, AiEffort effort, Guid aiSession, bool resume) => service switch
    {
        AiService.Codex => new CodexConversation(config, folder, model, effort, resume ? aiSession : null, workInstruction, freezer),
        _ => StartClaude(config, folder, model, effort, aiSession, resume)
    };

    private ClaudeConversation StartClaude(ServerConfig config, string folder, AiModel model, AiEffort effort, Guid aiSession, bool resume)
    {
        ProcessStartInfo start = new(config.ClaudeCommand)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.Environment["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1";

        foreach (string argument in (string[])["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions", "--disallowedTools", string.Join(',', claudeDisabledTools), "--append-system-prompt", workInstruction, "--model", model.ToString().ToLowerInvariant(), "--effort", effort.ToString().ToLowerInvariant(), .. config.ClaudeArguments, .. resume ? (string[])["--resume", aiSession.ToString()] : (string[])["--session-id", aiSession.ToString()]])
        {
            start.ArgumentList.Add(argument);
        }

        Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new RequestException($"Claude Code could not be started ('{config.ClaudeCommand}'): {exception.Message}");
        }

        return new ClaudeConversation(process, freezer);
    }
}
