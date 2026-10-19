namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Persists workspaces and their conversations on disk.
/// </summary>
internal interface IWorkspaceStore
{
    /// <summary>
    /// Points the store at its files.
    /// </summary>
    /// <param name="path">The workspaces file; conversations are kept in a <c>history</c> folder beside it.</param>
    void Open(string path);

    /// <summary>
    /// Reads the stored groups and workspaces.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The catalog, empty if nothing was stored yet.</returns>
    /// <exception cref="InvalidDataException">The workspaces file is not valid.</exception>
    Task<WorkspaceCatalog> Load(CancellationToken cancellation);

    /// <summary>
    /// Replaces the stored groups and workspaces.
    /// </summary>
    /// <param name="catalog">The catalog to store.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Save(WorkspaceCatalog catalog, CancellationToken cancellation);

    /// <summary>
    /// Reads the AI work that was paused when the server last stopped.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The paused work, empty if there is none or the file is not valid.</returns>
    Task<IReadOnlyList<PausedWork>> LoadPaused(CancellationToken cancellation);

    /// <summary>
    /// Replaces the stored paused AI work.
    /// </summary>
    /// <param name="paused">The paused work; an empty list removes the file.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SavePaused(IReadOnlyList<PausedWork> paused, CancellationToken cancellation);

    /// <summary>
    /// Reads a workspace's conversation.
    /// </summary>
    /// <param name="id">The workspace.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The messages in order, empty if there are none.</returns>
    Task<IReadOnlyList<ChatMessage>> ReadHistory(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Appends a message to a workspace's conversation.
    /// </summary>
    /// <param name="id">The workspace.</param>
    /// <param name="message">The message to add.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>The position of the message in the conversation, starting at zero.</returns>
    Task<int> AppendHistory(Guid id, ChatMessage message, CancellationToken cancellation);

    /// <summary>
    /// Stores a file attached to a prompt in the workspace's attachments folder beside the workspaces file.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The file name; any folders in it are dropped.</param>
    /// <param name="data">The file content.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>The full path of the stored file.</returns>
    Task<string> SaveAttachment(Guid id, string name, ReadOnlyMemory<byte> data, CancellationToken cancellation);

    /// <summary>
    /// Deletes a workspace's conversation and its attachments.
    /// </summary>
    /// <param name="id">The workspace.</param>
    void DeleteHistory(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceStore : IWorkspaceStore
{
    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

    private string path = "workspaces.json";

    /// <inheritdoc />
    public void Open(string path) => this.path = Path.GetFullPath(path);

    /// <inheritdoc />
    public async Task<WorkspaceCatalog> Load(CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceCatalog { Groups = [], Workspaces = [] };
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WorkspaceCatalog>(stream, options, cancellation) ?? new WorkspaceCatalog { Groups = [], Workspaces = [] };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Workspaces file '{path}' is invalid: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public async Task Save(WorkspaceCatalog catalog, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(catalog, options), cancellation);
            File.Move(temporary, path, true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PausedWork>> LoadPaused(CancellationToken cancellation)
    {
        string file = PausedPath();

        if (!File.Exists(file))
        {
            return [];
        }

        await gate.WaitAsync(cancellation);

        try
        {
            await using FileStream stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<PausedWork>>(stream, options, cancellation) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SavePaused(IReadOnlyList<PausedWork> paused, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            string file = PausedPath();

            if (paused.Count == 0)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(paused, options), cancellation);
            File.Move(temporary, file, true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> ReadHistory(Guid id, CancellationToken cancellation)
    {
        string file = HistoryPath(id);

        if (!File.Exists(file))
        {
            return [];
        }

        await gate.WaitAsync(cancellation);

        try
        {
            List<ChatMessage> messages = [];

            foreach (string line in await File.ReadAllLinesAsync(file, cancellation))
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<ChatMessage>(line, options) is { } message)
                {
                    messages.Add(message);
                }
            }

            return messages;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> AppendHistory(Guid id, ChatMessage message, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            string file = HistoryPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            int index = File.Exists(file) ? File.ReadLines(file).Count(line => line.Length > 0) : 0;
            await File.AppendAllTextAsync(file, JsonSerializer.Serialize(message, new JsonSerializerOptions(options) { WriteIndented = false }) + "\n", cancellation);
            return index;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> SaveAttachment(Guid id, string name, ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        string clean = string.Concat(name.Split(['/', '\\']).Last().Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        string folder = AttachmentsPath(id);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"{Guid.NewGuid():N}"[..8] + "-" + (clean.Length == 0 ? "file" : clean));
        await File.WriteAllBytesAsync(file, data, cancellation);
        return file;
    }

    /// <inheritdoc />
    public void DeleteHistory(Guid id)
    {
        if (File.Exists(HistoryPath(id)))
        {
            File.Delete(HistoryPath(id));
        }

        if (Directory.Exists(AttachmentsPath(id)))
        {
            Directory.Delete(AttachmentsPath(id), true);
        }
    }

    private string PausedPath() => Path.Combine(Path.GetDirectoryName(path)!, "paused.json");

    private string AttachmentsPath(Guid id) => Path.Combine(Path.GetDirectoryName(path)!, "attachments", id.ToString("N"));

    private string HistoryPath(Guid id) => Path.Combine(Path.GetDirectoryName(path)!, "history", id.ToString("N") + ".jsonl");
}
