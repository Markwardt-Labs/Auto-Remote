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
}

/// <inheritdoc />
internal sealed class WorkspaceStore : IWorkspaceStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceStore" /> class.
    /// </summary>
    public WorkspaceStore() => file = new StoredFile([UpgradeToTwo, UpgradeToThree]);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);

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
