namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Keeps what was typed in each workspace's message box on the device, so unsent text survives the app closing.
/// </summary>
internal interface IDraftStore
{
    /// <summary>
    /// Reads the saved draft of a workspace.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The draft, or an empty string when none is saved.</returns>
    Task<string> Load(Guid workspaceId, CancellationToken cancellation);

    /// <summary>
    /// Saves the draft of a workspace; an empty draft removes it.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="text">The draft.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Save(Guid workspaceId, string text, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class DraftStore(string path) : IDraftStore
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<string> Load(Guid workspaceId, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            return (await Read(cancellation)).GetValueOrDefault(workspaceId.ToString("N")) ?? string.Empty;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task Save(Guid workspaceId, string text, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            Dictionary<string, string> drafts = await Read(cancellation);
            string key = workspaceId.ToString("N");

            if (text.Length == 0 ? !drafts.Remove(key) : drafts.TryGetValue(key, out string? existing) && existing == text)
            {
                return;
            }

            if (text.Length > 0)
            {
                drafts[key] = text;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(drafts, options), cancellation);
            File.Move(temporary, path, true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> Read(CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, options, cancellation) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }
}
