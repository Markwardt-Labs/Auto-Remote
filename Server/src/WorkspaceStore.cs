namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Persists workspaces and the files attached to their prompts on disk.
/// </summary>
internal interface IWorkspaceStore
{
    /// <summary>
    /// Points the store at its files.
    /// </summary>
    /// <param name="path">The workspaces file; attachments are kept in an <c>attachments</c> folder beside it.</param>
    void Open(string path);

    /// <summary>
    /// Reads the stored groups and workspaces.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The catalog, empty if nothing was stored yet.</returns>
    /// <exception cref="InvalidDataException">The workspaces file is not valid.</exception>
    Task<StoredCatalog> Load(CancellationToken cancellation);

    /// <summary>
    /// Replaces the stored groups and workspaces.
    /// </summary>
    /// <param name="catalog">The catalog to store.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Save(StoredCatalog catalog, CancellationToken cancellation);

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
    /// Deletes a workspace's attachments.
    /// </summary>
    /// <param name="id">The workspace.</param>
    void DeleteAttachments(Guid id);

    /// <summary>
    /// Reads the chat of a workspace, which Auto Remote keeps itself beside the workspaces file.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The messages, oldest first, or <see langword="null" /> when the workspace has no chat record yet.</returns>
    Task<IReadOnlyList<ChatMessage>?> LoadMessages(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Adds a message to the end of a workspace's chat, making the record when there is none.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task AppendMessage(Guid id, ChatMessage message, CancellationToken cancellation);

    /// <summary>
    /// Replaces a workspace's whole chat.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="messages">The messages, oldest first.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SaveMessages(Guid id, IReadOnlyList<ChatMessage> messages, CancellationToken cancellation);

    /// <summary>
    /// Deletes a workspace's chat.
    /// </summary>
    /// <param name="id">The workspace.</param>
    void DeleteMessages(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceStore : IWorkspaceStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceStore" /> class.
    /// </summary>
    /// <param name="maxMessages">The most messages a workspace's chat keeps; the oldest are purged beyond it.</param>
    /// <param name="maxBytes">The most bytes a workspace's chat file keeps; the oldest messages are purged beyond it.</param>
    public WorkspaceStore(int maxMessages = 5000, long maxBytes = 16 * 1024 * 1024)
    {
        this.maxMessages = maxMessages;
        this.maxBytes = maxBytes;
        file = new StoredFile([UpgradeToTwo, UpgradeToThree]);
    }

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly JsonSerializerOptions lines = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly SemaphoreSlim chats = new(1, 1);

    private readonly ConcurrentDictionary<Guid, int> counts = new();

    private readonly int maxMessages;

    private readonly long maxBytes;

    private readonly IStoredFile file;

    private string path = "workspaces.json";

    /// <inheritdoc />
    public void Open(string path) => this.path = Path.GetFullPath(path);

    /// <inheritdoc />
    public async Task<StoredCatalog> Load(CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return new StoredCatalog();
        }

        JsonObject? content = await file.Read(path, cancellation);

        try
        {
            return content?.Deserialize<StoredCatalog>(options) ?? new StoredCatalog();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Workspaces file '{path}' is invalid: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public async Task Save(StoredCatalog catalog, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);

        try
        {
            await file.Write(path, JsonSerializer.SerializeToNode(catalog, options)!.AsObject(), cancellation);
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
    public void DeleteAttachments(Guid id)
    {
        if (Directory.Exists(AttachmentsPath(id)))
        {
            Directory.Delete(AttachmentsPath(id), true);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>?> LoadMessages(Guid id, CancellationToken cancellation)
    {
        await chats.WaitAsync(cancellation);

        try
        {
            if (!File.Exists(ChatPath(id)))
            {
                return null;
            }

            List<ChatMessage> messages = [];

            foreach (string line in await File.ReadAllLinesAsync(ChatPath(id), cancellation))
            {
                try
                {
                    if (line.Length > 0 && JsonSerializer.Deserialize<ChatMessage>(line, options) is { } message)
                    {
                        messages.Add(message);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return messages;
        }
        finally
        {
            chats.Release();
        }
    }

    /// <inheritdoc />
    public async Task AppendMessage(Guid id, ChatMessage message, CancellationToken cancellation)
    {
        await chats.WaitAsync(cancellation);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChatPath(id))!);
            int before = counts.TryGetValue(id, out int known) ? known : File.Exists(ChatPath(id)) ? File.ReadLines(ChatPath(id)).Count() : 0;
            await File.AppendAllTextAsync(ChatPath(id), JsonSerializer.Serialize(message, lines) + "\n", cancellation);
            counts[id] = before + 1;

            if (before + 1 > maxMessages + maxMessages / 5)
            {
                await Rewrite(id, await File.ReadAllLinesAsync(ChatPath(id), cancellation), cancellation);
            }
        }
        finally
        {
            chats.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveMessages(Guid id, IReadOnlyList<ChatMessage> messages, CancellationToken cancellation)
    {
        await chats.WaitAsync(cancellation);

        try
        {
            await Rewrite(id, [.. messages.Select(message => JsonSerializer.Serialize(message, lines))], cancellation);
        }
        finally
        {
            chats.Release();
        }
    }

    /// <inheritdoc />
    public void DeleteMessages(Guid id)
    {
        counts.TryRemove(id, out _);

        if (File.Exists(ChatPath(id)))
        {
            File.Delete(ChatPath(id));
        }
    }

    private async Task Rewrite(Guid id, IReadOnlyList<string> all, CancellationToken cancellation)
    {
        int first = all.Count;
        long size = 0;

        while (first > 0 && all.Count - first < maxMessages)
        {
            size += Encoding.UTF8.GetByteCount(all[first - 1]) + 1;

            if (size > maxBytes && first < all.Count)
            {
                break;
            }

            first--;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ChatPath(id))!);
        string temporary = ChatPath(id) + ".tmp";
        await File.WriteAllLinesAsync(temporary, all.Skip(first), cancellation);
        File.Move(temporary, ChatPath(id), true);
        counts[id] = all.Count - first;
    }

    private string ChatPath(Guid id) => Path.Combine(Path.GetDirectoryName(path)!, "chats", id.ToString("N") + ".jsonl");

    private string AttachmentsPath(Guid id) => Path.Combine(Path.GetDirectoryName(path)!, "attachments", id.ToString("N"));

    private void UpgradeToTwo(JsonObject content)
    {
        foreach (JsonObject workspace in (content["workspaces"] as JsonArray ?? []).OfType<JsonObject>())
        {
            workspace.Remove("model");
            workspace.Remove("effort");

            if (workspace["service"]?.GetValue<string>() == "OpenCode")
            {
                workspace["service"] = "Local";
            }
        }
    }

    private void UpgradeToThree(JsonObject content)
    {
        foreach (JsonObject workspace in (content["workspaces"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? session = workspace["aiSessionId"]?.GetValue<string>();
            bool usable = session is { Length: 32 } && !session.Contains('-', StringComparison.Ordinal);
            workspace.Remove("aiSessionId");
            workspace["sessionId"] = usable ? session : null;

            if (usable)
            {
                workspace.Remove("folder");
                workspace.Remove("service");
                workspace["adopt"] = true;
            }
        }
    }
}
