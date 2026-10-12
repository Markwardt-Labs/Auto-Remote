namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A conversation with Codex. Codex takes one prompt per process, so prompts are queued and run one after another in the same Codex session; prompts sent while it works run right after the current one.
/// </summary>
/// <param name="config">The server settings naming the Codex executable and arguments.</param>
/// <param name="folder">The working folder.</param>
/// <param name="model">The model to run on.</param>
/// <param name="effort">The effort to run with.</param>
/// <param name="session">The identifier of the Codex session to resume, or <see langword="null" /> to start one with the first prompt.</param>
internal sealed class CodexConversation(ServerConfig config, string folder, AiModel model, AiEffort effort, Guid? session) : IAiConversation
{
    private readonly Channel<AiOutput> outputs = Channel.CreateUnbounded<AiOutput>();

    private readonly Queue<string> prompts = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly object gate = new();

    private string? sessionId = session?.ToString();

    private Process? current;

    private Task worker = Task.CompletedTask;

    private bool running;

    private bool disposed;

    /// <inheritdoc />
    public Task Send(string text, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (disposed)
            {
                throw new RequestException("Codex is no longer running.");
            }

            prompts.Enqueue(text);

            if (!running)
            {
                running = true;
                worker = Task.Run(Work, CancellationToken.None);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AiOutput> Read([EnumeratorCancellation] CancellationToken cancellation)
    {
        await foreach (AiOutput output in outputs.Reader.ReadAllAsync(cancellation))
        {
            yield return output;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            prompts.Clear();
        }

        await stopping.CancelAsync();

        try
        {
            current?.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        await worker;
        outputs.Writer.TryComplete();
        stopping.Dispose();
    }

    private async Task Work()
    {
        while (true)
        {
            string text;

            lock (gate)
            {
                if (prompts.Count == 0 || disposed)
                {
                    running = false;
                    return;
                }

                text = prompts.Dequeue();
            }

            AiOutput? failure = await Run(text);

            lock (gate)
            {
                if (failure is not null)
                {
                    prompts.Clear();
                }

                if (failure is not null || prompts.Count == 0)
                {
                    running = false;
                    outputs.Writer.TryWrite(failure ?? new AiOutput { Kind = AiOutputKind.Finished });
                    return;
                }
            }
        }
    }

    private async Task<AiOutput?> Run(string text)
    {
        ProcessStartInfo start = new(config.CodexCommand)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in (string[])["exec", .. sessionId is null ? (string[])[] : ["resume"], "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "-m", Slug(), "-c", $"model_reasoning_effort=\"{effort.ToString().ToLowerInvariant()}\"", .. config.CodexArguments, .. sessionId is null ? (string[])[] : [sessionId], "-"])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new AiOutput { Kind = AiOutputKind.Failed, Text = $"Codex could not be started ('{config.CodexCommand}'): {exception.Message}" };
        }

        current = process;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string? failure = null;
        bool completed = false;

        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), stopping.Token);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(stopping.Token) is { } line)
            {
                if (Parse(line) is not { } output)
                {
                    continue;
                }

                switch (output.Kind)
                {
                    case AiOutputKind.Finished:
                        completed = true;
                        break;
                    case AiOutputKind.Failed:
                        failure ??= output.Text;
                        break;
                    default:
                        outputs.Writer.TryWrite(output);
                        break;
                }
            }

            await process.WaitForExitAsync(stopping.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return new AiOutput { Kind = AiOutputKind.Failed, Text = "Codex stopped." };
        }
        finally
        {
            current = null;
        }

        if (failure is null && (completed || process.ExitCode == 0))
        {
            return null;
        }

        string detail = (await errors).Trim();
        return new AiOutput { Kind = AiOutputKind.Failed, Text = failure ?? (detail.Length > 0 ? detail : "Codex stopped.") };
    }

    private AiOutput? Parse(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type))
            {
                return null;
            }

            switch (type.GetString())
            {
                case "thread.started" when root.TryGetProperty("thread_id", out JsonElement thread) && thread.GetString() is { Length: > 0 } id:
                    sessionId = id;
                    return new AiOutput { Kind = AiOutputKind.Session, Text = id };
                case "item.completed" when root.GetProperty("item") is { } item && item.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "agent_message" && item.TryGetProperty("text", out JsonElement reply) && !string.IsNullOrWhiteSpace(reply.GetString()):
                    return new AiOutput { Kind = AiOutputKind.Reply, Text = reply.GetString()! };
                case "turn.completed":
                    return new AiOutput { Kind = AiOutputKind.Finished };
                case "turn.failed":
                    return new AiOutput { Kind = AiOutputKind.Failed, Text = Failure(root) };
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Failure(JsonElement turn)
    {
        string message = turn.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement text) && text.GetString() is { Length: > 0 } found ? found : "Codex failed.";

        try
        {
            using JsonDocument inner = JsonDocument.Parse(message);
            return inner.RootElement.TryGetProperty("error", out JsonElement detail) && detail.TryGetProperty("message", out JsonElement nested) && nested.GetString() is { Length: > 0 } unwrapped ? unwrapped : message;
        }
        catch (JsonException)
        {
            return message;
        }
    }

    private string Slug() => model switch
    {
        AiModel.Sol => "gpt-5.6-sol",
        AiModel.Luna => "gpt-5.6-luna",
        _ => "gpt-5.6-terra"
    };
}
