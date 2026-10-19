namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A live Claude Code process taking prompts on its input stream.
/// </summary>
internal sealed class ClaudeConversation : IAiConversation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeConversation" /> class.
    /// </summary>
    /// <param name="process">The started Claude Code process with redirected streams.</param>
    /// <param name="freezer">Suspends and continues the process and what it started.</param>
    public ClaudeConversation(Process process, IProcessFreezer freezer)
    {
        this.process = process;
        this.freezer = freezer;
        errors = process.StandardError.ReadToEndAsync();
    }

    private readonly Process process;

    private readonly IProcessFreezer freezer;

    private readonly Task<string> errors;

    private readonly SemaphoreSlim writing = new(1, 1);

    /// <inheritdoc />
    public async Task Send(string text, CancellationToken cancellation)
    {
        string line = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });

        await writing.WaitAsync(cancellation);

        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
            await process.StandardInput.FlushAsync(cancellation);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new RequestException("Claude Code is no longer running.");
        }
        finally
        {
            writing.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AiOutput> Read([EnumeratorCancellation] CancellationToken cancellation)
    {
        bool failed = false;

        while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
        {
            if (Parse(line) is { } output)
            {
                failed |= output.Kind == AiOutputKind.Failed;
                yield return output;
            }
        }

        await process.WaitForExitAsync(cancellation);

        if (process.ExitCode != 0 && !failed)
        {
            string detail = (await errors).Trim();
            yield return new AiOutput { Kind = AiOutputKind.Failed, Text = detail.Length > 0 ? detail : "Claude Code stopped." };
        }
    }

    /// <inheritdoc />
    public Task Pause() => freezer.Freeze(process.Id);

    /// <inheritdoc />
    public Task Continue() => freezer.Thaw(process.Id);

    /// <inheritdoc />
    public void Kill()
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        using CancellationTokenSource grace = new(TimeSpan.FromSeconds(3));

        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
        }

        process.Dispose();
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
                case "assistant" when !root.TryGetProperty("parent_tool_use_id", out JsonElement parent) || parent.ValueKind == JsonValueKind.Null:
                    string text = string.Join("\n\n", root.GetProperty("message").GetProperty("content").EnumerateArray()
                        .Where(block => block.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "text" && block.TryGetProperty("text", out _))
                        .Select(block => block.GetProperty("text").GetString())
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
                    return text.Length > 0 ? new AiOutput { Kind = AiOutputKind.Reply, Text = text } : null;
                case "result":
                    return root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True
                        ? new AiOutput { Kind = AiOutputKind.Failed, Text = Failure(root) }
                        : new AiOutput { Kind = AiOutputKind.Finished };
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private string Failure(JsonElement result)
    {
        if (result.TryGetProperty("result", out JsonElement text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
        {
            return text.GetString()!;
        }

        if (result.TryGetProperty("errors", out JsonElement list) && list.ValueKind == JsonValueKind.Array && string.Join("\n", list.EnumerateArray().Select(item => item.GetString())) is { Length: > 0 } joined)
        {
            return joined;
        }

        return "Claude Code failed.";
    }
}
