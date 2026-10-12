namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Owns the workspaces and the groups they are organized into, and keeps them stored.
/// </summary>
internal interface IWorkspaceManager
{
    /// <summary>
    /// Loads the stored workspaces.
    /// </summary>
    /// <param name="path">The workspaces file.</param>
    /// <param name="cancellation">Cancels the load.</param>
    /// <exception cref="InvalidDataException">The workspaces file is not valid.</exception>
    Task Open(string path, CancellationToken cancellation);

    /// <summary>
    /// Lists the groups and the workspaces, each ordered by name.
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
    /// Creates a workspace hosted by an existing folder.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The new workspace.</returns>
    /// <exception cref="RequestException">The folder is missing or already hosts a workspace.</exception>
    Task<WorkspaceInfo> Create(string folder, CancellationToken cancellation);

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
    /// Changes the AI service, model and effort a workspace's prompts run with.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="service">The AI service; changing it detaches the workspace's AI session, since sessions belong to one service.</param>
    /// <param name="model">The model.</param>
    /// <param name="effort">The effort.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The updated workspace.</returns>
    /// <exception cref="RequestException">The workspace is unknown, or the service does not offer the model or effort.</exception>
    Task<WorkspaceInfo> Configure(Guid id, AiService service, AiModel model, AiEffort effort, CancellationToken cancellation);

    /// <summary>
    /// Attaches an AI session to a workspace, replacing any earlier one.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="aiSessionId">The AI session identifier.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The workspace is unknown.</exception>
    Task AttachAiSession(Guid id, Guid aiSessionId, CancellationToken cancellation);

    /// <summary>
    /// Moves a workspace into a group, or out of any group.
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
internal sealed class WorkspaceManager(IWorkspaceStore store, IFolderBrowser folders) : IWorkspaceManager
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly List<WorkspaceInfo> workspaces = [];

    private readonly List<WorkspaceGroup> groups = [];

    private readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public async Task Open(string path, CancellationToken cancellation)
    {
        store.Open(path);
        WorkspaceCatalog loaded = await store.Load(cancellation);

        using (await Lock(cancellation))
        {
            workspaces.Clear();
            workspaces.AddRange(loaded.Workspaces);
            groups.Clear();
            groups.AddRange(loaded.Groups);
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceCatalog> List(CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
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
    public async Task<WorkspaceInfo> Create(string folder, CancellationToken cancellation)
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

            WorkspaceInfo created = new() { Id = Guid.NewGuid(), Folder = full };
            workspaces.Add(created);
            await store.Save(Snapshot(), cancellation);
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

            WorkspaceInfo renamed = workspace with { Folder = await folders.Rename(workspace.Folder, name, cancellation) };
            workspaces[workspaces.IndexOf(workspace)] = renamed;
            await store.Save(Snapshot(), cancellation);
            return renamed;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceInfo> Configure(Guid id, AiService service, AiModel model, AiEffort effort, CancellationToken cancellation)
    {
        if (!service.Models().Contains(model) || !service.Efforts().Contains(effort))
        {
            throw new RequestException($"{service.Label()} does not offer {model} at {effort} effort.");
        }

        using (await Lock(cancellation))
        {
            WorkspaceInfo workspace = Find(id);
            return await Replace(workspace with { Service = service, Model = model, Effort = effort, AiSessionId = service == workspace.Service ? workspace.AiSessionId : null }, cancellation);
        }
    }

    /// <inheritdoc />
    public async Task AttachAiSession(Guid id, Guid aiSessionId, CancellationToken cancellation)
    {
        using (await Lock(cancellation))
        {
            await Replace(Find(id) with { AiSessionId = aiSessionId }, cancellation);
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

            return await Replace(workspace with { GroupId = groupId }, cancellation);
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
            await store.Save(Snapshot(), cancellation);
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
            await store.Save(Snapshot(), cancellation);
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
            groups[groups.IndexOf(group)] = moved;
            await store.Save(Snapshot(), cancellation);
            return moved;
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

            await store.Save(Snapshot(), cancellation);
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
            await store.Save(Snapshot(), cancellation);
            store.DeleteHistory(id);
        }
    }

    private async Task<IDisposable> Lock(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        return new Releaser(gate);
    }

    private WorkspaceCatalog Snapshot() => new()
    {
        Groups = [.. groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)],
        Workspaces = [.. workspaces.OrderBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)]
    };

    private async Task<WorkspaceInfo> Replace(WorkspaceInfo updated, CancellationToken cancellation)
    {
        workspaces[workspaces.FindIndex(workspace => workspace.Id == updated.Id)] = updated;
        await store.Save(Snapshot(), cancellation);
        return updated;
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
