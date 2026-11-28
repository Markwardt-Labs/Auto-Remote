namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What the swarm stores, kept in the <c>swarm</c> folder of the data folder, with the identity of the server. Every change is made under one lock and written at once, and tells clients that the swarm changed.
/// </summary>
internal interface ISwarmBook
{
    /// <summary>
    /// Gets the <c>swarm</c> folder.
    /// </summary>
    string Folder { get; }

    /// <summary>
    /// Gets the identity of this server.
    /// </summary>
    ISwarmIdentity Identity { get; }

    /// <summary>
    /// Loads what is stored, making the identity on the first use.
    /// </summary>
    /// <param name="dataFolder">The data folder.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <returns>A task that completes once loaded.</returns>
    Task Open(string dataFolder, CancellationToken cancellation);

    /// <summary>
    /// Calls back with what is stored once it is loaded and after every change, while nothing else can read or change it, so what is derived from it never lags behind.
    /// </summary>
    /// <param name="watcher">Derives it; it must be quick and must not use the book.</param>
    void Watch(Action<SwarmDocument> watcher);

    /// <summary>
    /// Reads from what is stored; what is read must not be changed or kept.
    /// </summary>
    /// <typeparam name="T">What is read.</typeparam>
    /// <param name="read">Reads it.</param>
    /// <returns>What was read.</returns>
    Task<T> Read<T>(Func<SwarmDocument, T> read);

    /// <summary>
    /// Changes what is stored.
    /// </summary>
    /// <typeparam name="T">What the change gives back.</typeparam>
    /// <param name="change">Changes it.</param>
    /// <param name="announce">Whether clients are told.</param>
    /// <returns>What the change gave back.</returns>
    Task<T> Change<T>(Func<SwarmDocument, T> change, bool announce = true);

    /// <summary>
    /// Changes what is stored.
    /// </summary>
    /// <param name="change">Changes it.</param>
    /// <param name="announce">Whether clients are told.</param>
    /// <returns>A task that completes once stored.</returns>
    Task Change(Action<SwarmDocument> change, bool announce = true);

    /// <summary>
    /// Reads the conversation kept for an outgoing task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<ChatMessage>> LoadChat(Guid taskId);

    /// <summary>
    /// Keeps the conversation of an outgoing task and tells clients that the task changed.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="messages">The messages.</param>
    /// <returns>A task that completes once stored.</returns>
    Task SaveChat(Guid taskId, IReadOnlyList<ChatMessage> messages);

    /// <summary>
    /// Deletes the conversation of an outgoing task.
    /// </summary>
    /// <param name="taskId">The task.</param>
    void DeleteChat(Guid taskId);

    /// <summary>
    /// Tells clients that the swarm changed.
    /// </summary>
    void Announce();

    /// <summary>
    /// Tells clients that one task changed.
    /// </summary>
    /// <param name="taskId">The task.</param>
    void AnnounceTask(Guid taskId);
}

/// <inheritdoc />
internal sealed class SwarmBook(IEventHub events, ISwarmIdentity.IFactory identities) : ISwarmBook
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly SemaphoreSlim chats = new(1, 1);

    private readonly IStoredFile file = new StoredFile([], true);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<Action<SwarmDocument>> watchers = [];

    private SwarmDocument document = new();

    private ISwarmIdentity? identity;

    private string? folder;

    /// <inheritdoc />
    public string Folder => folder ?? throw new InvalidOperationException("The swarm is not set up yet.");

    /// <inheritdoc />
    public ISwarmIdentity Identity => identity ?? throw new InvalidOperationException("The swarm is not set up yet.");

    /// <inheritdoc />
    public async Task Open(string dataFolder, CancellationToken cancellation)
    {
        string root = Path.GetFullPath(Path.Combine(dataFolder, "swarm"));
        Directory.CreateDirectory(root);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        identity = await identities.Load(Path.Combine(root, "identity.json"), cancellation);

        if (await file.Read(Path.Combine(root, "swarm.json"), cancellation) is { } content)
        {
            document = content.Deserialize<SwarmDocument>(options) ?? new SwarmDocument();
        }

        if (string.IsNullOrWhiteSpace(document.Server.Name))
        {
            document.Server = document.Server with { Name = Environment.MachineName };
        }

        folder = root;
        await gate.WaitAsync(cancellation);

        try
        {
            watchers.ForEach(watcher => watcher(document));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Watch(Action<SwarmDocument> watcher) => watchers.Add(watcher);

    /// <inheritdoc />
    public async Task<T> Read<T>(Func<SwarmDocument, T> read)
    {
        await gate.WaitAsync();

        try
        {
            return read(document);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<T> Change<T>(Func<SwarmDocument, T> change, bool announce = true)
    {
        T result;
        await gate.WaitAsync();

        try
        {
            result = change(document);
            watchers.ForEach(watcher => watcher(document));
            await file.Write(Path.Combine(Folder, "swarm.json"), JsonSerializer.SerializeToNode(document, options)!.AsObject(), CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }

        if (announce)
        {
            Announce();
        }

        return result;
    }

    /// <inheritdoc />
    public Task Change(Action<SwarmDocument> change, bool announce = true) => Change<bool>(stored =>
    {
        change(stored);
        return true;
    }, announce);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> LoadChat(Guid taskId)
    {
        await chats.WaitAsync();

        try
        {
            string path = ChatPath(taskId);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<ChatMessage>>(await File.ReadAllTextAsync(path), options) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            chats.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveChat(Guid taskId, IReadOnlyList<ChatMessage> messages)
    {
        await chats.WaitAsync();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChatPath(taskId))!);
            await File.WriteAllTextAsync(ChatPath(taskId), JsonSerializer.Serialize(messages, options));
        }
        finally
        {
            chats.Release();
        }

        AnnounceTask(taskId);
    }

    /// <inheritdoc />
    public void DeleteChat(Guid taskId)
    {
        // Deleting throws where the folder was never made, as before the first conversation arrived.
        if (File.Exists(ChatPath(taskId)))
        {
            File.Delete(ChatPath(taskId));
        }
    }

    /// <inheritdoc />
    public void Announce() => events.Publish(new RemoteEvent { Kind = RemoteEventKind.SwarmChanged, WorkspaceId = Guid.Empty });

    /// <inheritdoc />
    public void AnnounceTask(Guid taskId) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.SwarmTaskChanged, WorkspaceId = Guid.Empty, TaskId = taskId });

    private string ChatPath(Guid taskId) => Path.Combine(Folder, "chats", $"{taskId:N}.json");
}

/// <summary>
/// Records paired servers in the book.
/// </summary>
internal static class SwarmBookExtensions
{
    extension(ISwarmBook book)
    {
        /// <summary>
        /// Records a server as paired. Pairing again keeps the settings; everything else starts off, so nothing is sent or accepted until the user turns it on.
        /// </summary>
        /// <param name="identity">Its public keys.</param>
        /// <param name="name">Its name.</param>
        /// <param name="mode">The delivery mode a new pairing starts with.</param>
        /// <param name="address">The address to connect to, or empty.</param>
        /// <returns>A task that completes once recorded.</returns>
        /// <exception cref="RequestException">It is this server itself.</exception>
        public Task Pair(SwarmPublicIdentity identity, string name, SwarmMode mode, string address)
        {
            if (identity.Id == book.Identity.Public.Id)
            {
                throw new RequestException("A server cannot pair with itself.");
            }

            string shown = name.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, 100)] : "Unnamed server";
            return book.Change(document =>
            {
                if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == identity.Id) is { } existing)
                {
                    existing.Name = shown;

                    if (address.Length > 0)
                    {
                        existing.Address = address;
                        existing.Mode = mode;
                    }

                    return;
                }

                document.Peers.Add(new SwarmPeerRecord { Identity = identity, Name = shown, Mode = mode, Address = address, PairedAt = DateTimeOffset.UtcNow });
            });
        }
    }
}
