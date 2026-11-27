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

    /// <summary>
    /// Reads what auto commit of the draft is, which a draft that holds a saved input took with it.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>Whether the draft is committed after, or <see langword="null" /> when it holds no saved input.</returns>
    Task<bool?> LoadAutoCommit(Guid workspaceId, CancellationToken cancellation);

    /// <summary>
    /// Saves what auto commit of the draft is; <see langword="null" /> removes it.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="autoCommit">Whether the draft is committed after, or <see langword="null" /> when it holds no saved input.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SaveAutoCommit(Guid workspaceId, bool? autoCommit, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class DraftStore(string path) : IDraftStore
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

    /// <inheritdoc />
    public async Task<string> Load(Guid workspaceId, CancellationToken cancellation) => await Get(Key(workspaceId), cancellation);

    /// <inheritdoc />
    public Task Save(Guid workspaceId, string text, CancellationToken cancellation) => Put(Key(workspaceId), text, cancellation);

    /// <inheritdoc />
    public async Task<bool?> LoadAutoCommit(Guid workspaceId, CancellationToken cancellation)
        => await Get(CommitKey(workspaceId), cancellation) switch { "on" => true, "off" => false, _ => null };

    /// <inheritdoc />
    public Task SaveAutoCommit(Guid workspaceId, bool? autoCommit, CancellationToken cancellation)
        => Put(CommitKey(workspaceId), autoCommit switch { true => "on", false => "off", null => string.Empty }, cancellation);

    private string Key(Guid workspaceId) => workspaceId.ToString("N");

    private string CommitKey(Guid workspaceId) => $"{Key(workspaceId)}:autocommit";

    private async Task<string> Get(string key, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            return (await Read(cancellation)).GetValueOrDefault(key) ?? string.Empty;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Put(string key, string text, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            Dictionary<string, string> drafts = await Read(cancellation);

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
