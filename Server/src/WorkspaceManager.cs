namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Owns the workspaces and the groups they are organized into, and keeps them stored.
/// </summary>
internal interface IWorkspaceManager
{
    /// <summary>
    /// Loads the stored workspaces and reads the folder and provider of each from its AI session; a workspace an older release stored without a usable session is given its first one. A stored workspace whose session cannot be found stays in the file but is not listed.
    /// </summary>
    /// <param name="path">The workspaces file.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <exception cref="InvalidDataException">The workspaces file is not valid.</exception>
    Task Open(string path, CancellationToken cancellation);

    /// <summary>
    /// Lists the groups and the workspaces, each in its custom order.
    /// </summary>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The catalog.</returns>
    Task<WorkspaceCatalog> List(CancellationToken cancellation);

    /// <summary>
    /// Finds a workspace.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The workspace.</returns>
    /// <exception cref="RequestException">No such workspace exists.</exception>
    Task<WorkspaceInfo> Get(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Creates a workspace hosted by an existing folder, with the AI session that holds its folder, provider and configuration.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="service">The AI service the workspace's prompts run on for good.</param>
    /// <param name="apiKey">An API key for just the workspace's session, or <see langword="null" />.</param>
    /// <param name="groupId">The group to file the workspace in, or <see langword="null" /> for the top level.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The new workspace.</returns>
    /// <exception cref="RequestException">The folder is missing or already hosts a workspace, the group no longer exists, or the session cannot be made.</exception>
    Task<WorkspaceInfo> Create(string folder, AiService service, string? apiKey, Guid? groupId, CancellationToken cancellation);

    /// <summary>
    /// Renames a workspace by renaming its folder.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="name">The new folder name.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The renamed workspace.</returns>
    /// <exception cref="RequestException">The workspace is unknown, the name is unusable, or another workspace lives inside the folder.</exception>
    Task<WorkspaceInfo> Rename(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Moves a workspace to another folder: tells the AI session that the workspace is there now, which is what tracks its place. No file is moved, so the folder must already exist.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="folder">The existing folder the workspace works in from now on.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The moved workspace.</returns>
    /// <exception cref="RequestException">The workspace is unknown, the folder does not exist or already hosts another workspace, or the session cannot be moved.</exception>
    Task<WorkspaceInfo> MoveToFolder(Guid id, string folder, CancellationToken cancellation);

    /// <summary>
    /// Attaches a live AI session to a workspace, replacing any earlier one.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="sessionId">The live AI session identifier.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The workspace is unknown.</exception>
    Task AttachSession(Guid id, string sessionId, CancellationToken cancellation);

    /// <summary>
    /// Sets the review and expand prompts a workspace has of its own, which its buttons send instead of the ones of the environment.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="reviewPrompt">The review prompt, or <see langword="null" /> or blank to use the one of the environment.</param>
    /// <param name="expandPrompt">The expand prompt, or <see langword="null" /> or blank to use the one of the environment.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="RequestException">The workspace is unknown.</exception>
    Task<WorkspaceInfo> SetPrompts(Guid id, string? reviewPrompt, string? expandPrompt, CancellationToken cancellation);

    /// <summary>
    /// Moves a workspace into a group, or out of any group; it goes to the end of that group.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="groupId">The group, or <see langword="null" /> for no group.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="RequestException">The workspace or group is unknown.</exception>
    Task<WorkspaceInfo> Move(Guid id, Guid? groupId, CancellationToken cancellation);

    /// <summary>
    /// Creates a group.
    /// </summary>
    /// <param name="name">The group's name.</param>
    /// <param name="parentId">The group to create it in, or <see langword="null" /> for the top level.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The new group.</returns>
    /// <exception cref="RequestException">The parent is unknown, or the name is empty or already used by another group in the same place.</exception>
    Task<WorkspaceGroup> CreateGroup(string name, Guid? parentId, CancellationToken cancellation);

    /// <summary>
    /// Renames a group.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The renamed group.</returns>
    /// <exception cref="RequestException">The group is unknown, or the name is empty or already used by another group in the same place.</exception>
    Task<WorkspaceGroup> RenameGroup(Guid id, string name, CancellationToken cancellation);

    /// <summary>
    /// Moves a group, with everything in it, into another group or to the top level.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="parentId">The new parent group, or <see langword="null" /> for the top level.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The moved group.</returns>
    /// <exception cref="RequestException">The group or parent is unknown, the parent is the group or inside it, or the name is already used in the new place.</exception>
    Task<WorkspaceGroup> MoveGroup(Guid id, Guid? parentId, CancellationToken cancellation);

    /// <summary>
    /// Moves a group up or down among the groups with the same parent.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="offset">How many places to move: negative is up, positive is down. It stops at the first or last place.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The group is unknown.</exception>
    Task ShiftGroup(Guid id, int offset, CancellationToken cancellation);

    /// <summary>
    /// Deletes a group; the workspaces and groups in it are kept and move into its parent.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The group is unknown.</exception>
    Task DeleteGroup(Guid id, CancellationToken cancellation);

    /// <summary>
    /// Deletes a workspace, optionally with its folder.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="deleteFolder">Whether to delete the workspace's folder and its contents too.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The workspace is unknown, or its folder is shared with another workspace or too important to delete.</exception>
    Task Delete(Guid id, bool deleteFolder, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class WorkspaceManager(IWorkspaceStore store, IFolderBrowser folders, IEventHub events, IPrompterHost host) : IWorkspaceManager
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly List<WorkspaceInfo> workspaces = [];

    private readonly List<WorkspaceGroup> groups = [];

    private readonly List<StoredWorkspace> unresolved = [];

    private readonly HashSet<Guid> adopting = [];

    private readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public async Task Open(string path, CancellationToken cancellation)
    {
        store.Open(path);
        StoredCatalog loaded = await store.Load(cancellation);
        List<StoredWorkspace> entries = [];
        bool changed = false;

        foreach (StoredWorkspace stored in loaded.Workspaces)
        {
            if (stored.SessionId is null && stored.Folder is { } folder)
            {
                try
                {
                    entries.Add(stored with { SessionId = await host.CreateSession(folder, stored.Service ?? AiService.ClaudeCode, null, cancellation), Folder = null, Service = null });
                    changed = true;
                    continue;
                }
                catch (RequestException)
                {
                }
            }

            if (stored is { Adopt: true, SessionId: { } adoptable })
            {
                try
                {
                    await host.Adopt(adoptable, cancellation);
                    entries.Add(stored with { Adopt = false });
                    changed = true;
                    continue;
                }
                catch (RequestException)
                {
                }
            }

            entries.Add(stored);
        }

        Dictionary<string, PromptSessionInfo> sessions = (await host.Prompter.List(cancellation)).ToDictionary(info => info.Id);

        using (await Lock(cancellation))
        {
            workspaces.Clear();
            unresolved.Clear();
            adopting.Clear();
            groups.Clear();
            groups.AddRange(loaded.Groups);

            foreach (StoredWorkspace stored in entries)
            {
                if (stored.SessionId is { } sessionId && sessions.TryGetValue(sessionId, out PromptSessionInfo? info) && info.WorkspacePath is { } folder)
                {
                    workspaces.Add(new WorkspaceInfo { Id = stored.Id, Folder = folder, Service = ServiceOf(info.Provider), SessionId = sessionId, GroupId = stored.GroupId, LastUpdated = info.LastLoaded, ReviewPrompt = stored.ReviewPrompt, ExpandPrompt = stored.ExpandPrompt });

                    if (stored.Adopt)
                    {
                        adopting.Add(stored.Id);
                    }
                }
                else
                {
                    unresolved.Add(stored);
                }
            }

            if (changed)
            {
                await Save(cancellation, false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceCatalog> List(CancellationToken cancellation)
    {
        Dictionary<string, PromptSessionInfo>? sessions = null;

        try
        {
            sessions = (await host.Prompter.List(cancellation)).ToDictionary(info => info.Id);
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or InvalidOperationException)
        {
        }

        using (await Lock(cancellation))
        {
            if (sessions is not null)
            {
                for (int index = 0; index < workspaces.Count; index++)
                {
                    if (workspaces[index].SessionId is { } sessionId && sessions.TryGetValue(sessionId, out PromptSessionInfo? info) && workspaces[index].LastUpdated != info.LastLoaded)
                    {
                        workspaces[index] = workspaces[index] with { LastUpdated = info.LastLoaded };
                    }
                }
            }

            return Snapshot();
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Get(Guid id, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            return Find(id);
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Create(string folder, AiService service, string? apiKey, Guid? groupId, CancellationToken cancellation)
    {
        if (!await folders.Exists(folder, cancellation))
        {
            throw new RequestException($"The folder '{folder}' does not exist.");
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        using (await Lock(cancellation))
        {
            if (workspaces.Any(workspace => string.Equals(workspace.Folder, full, comparison)))
            {
                throw new RequestException("This folder already hosts a workspace.");
            }

            if (groupId is { } target && groups.All(group => group.Id != target))
            {
                throw new RequestException("The group no longer exists.");
            }

            string sessionId = await host.CreateSession(full, service, apiKey, cancellation);
            WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = full, Service = service, SessionId = sessionId, GroupId = groupId, LastUpdated = await Loaded(sessionId, cancellation) };
            workspaces.Add(created);

            try
            {
                await Save(cancellation);
            }
            catch
            {
                workspaces.Remove(created);
                await Discard(sessionId);
                throw;
            }

            return created;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Rename(Guid id, string name, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);

            if (workspaces.Any(other => other.Id != id && IsInside(other.Folder, workspace.Folder)))
            {
                throw new RequestException("Another workspace lives inside this folder, so it cannot be renamed.");
            }

            string renamedFolder = await folders.Rename(workspace.Folder, name, cancellation);

            if (workspace.SessionId is { } sessionId)
            {
                try
                {
                    await host.MoveSession(sessionId, renamedFolder, cancellation);
                }
                catch (RequestException)
                {
                    await folders.Rename(renamedFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace.Folder)), CancellationToken.None);
                    throw;
                }
            }

            WorkspaceInfo renamed = workspace with { Folder = renamedFolder };
            workspaces[workspaces.IndexOf(workspace)] = renamed;
            await Save(cancellation);
            return renamed;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> MoveToFolder(Guid id, string folder, CancellationToken cancellation)
    {
        if (!await folders.Exists(folder, cancellation))
        {
            throw new RequestException($"The folder '{folder}' does not exist.");
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);

            if (string.Equals(workspace.Folder, full, comparison))
            {
                return workspace;
            }

            if (workspaces.Any(other => string.Equals(other.Folder, full, comparison)))
            {
                throw new RequestException("This folder already hosts a workspace.");
            }

            if (workspace.SessionId is { } sessionId)
            {
                await host.MoveSession(sessionId, full, cancellation);
            }

            WorkspaceInfo moved = workspace with { Folder = full };
            workspaces[workspaces.IndexOf(workspace)] = moved;
            await Save(cancellation);
            return moved;
        }
    }

    /// <inheritdoc />
    public async Task AttachSession(Guid id, string sessionId, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            await Replace(Find(id) with { SessionId = sessionId }, cancellation, false);
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> SetPrompts(Guid id, string? reviewPrompt, string? expandPrompt, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);
            string? review = string.IsNullOrWhiteSpace(reviewPrompt) ? null : reviewPrompt.Trim();
            string? expand = string.IsNullOrWhiteSpace(expandPrompt) ? null : expandPrompt.Trim();
            return review == workspace.ReviewPrompt && expand == workspace.ExpandPrompt ? workspace : await Replace(workspace with { ReviewPrompt = review, ExpandPrompt = expand }, cancellation, false);
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Move(Guid id, Guid? groupId, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);

            if (groupId is { } target && groups.All(group => group.Id != target))
            {
                throw new RequestException("The group no longer exists.");
            }

            if (groupId == workspace.GroupId)
            {
                return workspace;
            }

            WorkspaceInfo moved = workspace with { GroupId = groupId };
            workspaces.Remove(workspace);
            workspaces.Add(moved);
            await Save(cancellation);
            return moved;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceGroup> CreateGroup(string name, Guid? parentId, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            if (parentId is { } parent)
            {
                FindGroup(parent);
            }

            WorkspaceGroup created = new() { Id = Guid.NewGuid(), Name = ValidateGroupName(name, parentId, null), ParentId = parentId };
            groups.Add(created);
            await Save(cancellation);
            return created;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceGroup> RenameGroup(Guid id, string name, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceGroup group = FindGroup(id);
            WorkspaceGroup renamed = group with { Name = ValidateGroupName(name, group.ParentId, id) };
            groups[groups.IndexOf(group)] = renamed;
            await Save(cancellation);
            return renamed;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceGroup> MoveGroup(Guid id, Guid? parentId, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceGroup group = FindGroup(id);

            if (parentId is { } parent)
            {
                FindGroup(parent);

                for (Guid? ancestor = parent; ancestor is { } current; ancestor = groups.FirstOrDefault(other => other.Id == current)?.ParentId)
                {
                    if (current == id)
                    {
                        throw new RequestException("A group cannot be moved into itself.");
                    }
                }
            }

            ValidateGroupName(group.Name, parentId, id);
            WorkspaceGroup moved = group with { ParentId = parentId };

            if (parentId != group.ParentId)
            {
                groups.Remove(group);
                groups.Add(moved);
            }

            await Save(cancellation);
            return moved;
        }
    }

    /// <inheritdoc />
    public async Task ShiftGroup(Guid id, int offset, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceGroup group = FindGroup(id);
            Shift(groups, group, offset, other => other.ParentId == group.ParentId);
            await Save(cancellation);
        }
    }

    /// <inheritdoc />
    public async Task DeleteGroup(Guid id, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceGroup removed = FindGroup(id);
            groups.Remove(removed);

            for (int index = 0; index < groups.Count; index++)
            {
                if (groups[index].ParentId == id)
                {
                    groups[index] = groups[index] with { ParentId = removed.ParentId };
                }
            }

            for (int index = 0; index < workspaces.Count; index++)
            {
                if (workspaces[index].GroupId == id)
                {
                    workspaces[index] = workspaces[index] with { GroupId = removed.ParentId };
                }
            }

            await Save(cancellation);
        }
    }

    /// <inheritdoc />
    public async Task Delete(Guid id, bool deleteFolder, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);

            if (deleteFolder)
            {
                if (workspaces.Any(other => other.Id != id && (IsInside(other.Folder, workspace.Folder) || IsInside(workspace.Folder, other.Folder))))
                {
                    throw new RequestException("Another workspace uses this folder, so it cannot be deleted.");
                }

                await folders.Delete(workspace.Folder, cancellation);
            }

            workspaces.Remove(workspace);
            await Save(cancellation);
            store.DeleteAttachments(id);
        }
    }

    private async Task<IDisposable> Lock(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        return new Releaser(gate);
    }

    private AiService ServiceOf(PromptProvider provider)
        => provider switch
        {
            PromptProvider.Codex => AiService.Codex,
            PromptProvider.Local => AiService.Local,
            _ => AiService.ClaudeCode
        };

    private async Task Discard(string sessionId)
    {
        try
        {
            await host.Prompter.Delete(sessionId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<DateTimeOffset> Loaded(string sessionId, CancellationToken cancellation)
    {
        try
        {
            return (await host.Prompter.List(cancellation)).FirstOrDefault(info => info.Id == sessionId)?.LastLoaded ?? DateTimeOffset.UtcNow;
        }
        catch (Exception exception) when (exception is PromptSessionException or IOException or InvalidOperationException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private WorkspaceCatalog Snapshot() => new() { Groups = [.. groups], Workspaces = [.. workspaces] };

    private void Shift<T>(List<T> items, T item, int offset, Func<T, bool> isSibling)
    {
        int step = Math.Sign(offset);
        int position = items.IndexOf(item);

        for (int remaining = Math.Abs(offset); remaining > 0; remaining--)
        {
            int next = position + step;

            while (next >= 0 && next < items.Count && !isSibling(items[next]))
            {
                next += step;
            }

            if (next < 0 || next >= items.Count)
            {
                return;
            }

            (items[position], items[next]) = (items[next], items[position]);
            position = next;
        }
    }

    private async Task<WorkspaceInfo> Replace(WorkspaceInfo updated, CancellationToken cancellation, bool announce = true)
    {
        workspaces[workspaces.FindIndex(workspace => workspace.Id == updated.Id)] = updated;
        await Save(cancellation, announce);
        return updated;
    }

    private async Task Save(CancellationToken cancellation, bool announce = true)
    {
        await store.Save(new StoredCatalog { Groups = [.. groups], Workspaces = [.. workspaces.Select(workspace => new StoredWorkspace { Id = workspace.Id, SessionId = workspace.SessionId, GroupId = workspace.GroupId, ReviewPrompt = workspace.ReviewPrompt, ExpandPrompt = workspace.ExpandPrompt, Adopt = adopting.Contains(workspace.Id) }), .. unresolved] }, cancellation);

        if (announce)
        {
            events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkspacesChanged, WorkspaceId = Guid.Empty });
        }
    }

    private string ValidateGroupName(string name, Guid? parentId, Guid? ignore)
    {
        string trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            throw new RequestException("The group name is empty.");
        }

        if (groups.Any(group => group.Id != ignore && group.ParentId == parentId && string.Equals(group.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RequestException($"A group named '{trimmed}' already exists here.");
        }

        return trimmed;
    }

    private WorkspaceGroup FindGroup(Guid id) => groups.FirstOrDefault(group => group.Id == id) ?? throw new RequestException("The group no longer exists.");

    private WorkspaceInfo Find(Guid id) => workspaces.FirstOrDefault(workspace => workspace.Id == id) ?? throw new RequestException("The workspace no longer exists.");

    private bool IsInside(string child, string parent) => string.Equals(child, parent, comparison) || child.StartsWith(parent.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, comparison);

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
