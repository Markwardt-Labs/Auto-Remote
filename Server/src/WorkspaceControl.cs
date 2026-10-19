namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Pauses, resumes and ends the work running in a workspace on request: the AI's work can be paused, resumed, stopped (keeping what it did) or cancelled (reverting it), while a script or a git action can only be stopped.
/// </summary>
internal interface IWorkspaceControl
{
    /// <summary>
    /// Takes up the AI work that was paused when the server last stopped: those workspaces are paused again, so they can be resumed, stopped or cancelled, and from now on every change to what is paused is stored. Must be called once as the server starts.
    /// </summary>
    /// <param name="cancellation">Cancelled when the server stops, after which nothing more is stored, since stopping the server ends the AI processes and must not look like the work ending.</param>
    /// <returns>A task that completes once the paused work is back.</returns>
    Task Restore(CancellationToken cancellation);

    /// <summary>
    /// Pauses the AI's work in a workspace: it is suspended where it is and the workspace is <see cref="WorkspaceState.Paused" /> until it is resumed, stopped or cancelled.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the AI is paused.</returns>
    /// <exception cref="RequestException">The AI is not working in the workspace, or could not be paused.</exception>
    Task Pause(Guid id);

    /// <summary>
    /// Lets the paused AI work in a workspace carry on.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the AI works again.</returns>
    /// <exception cref="RequestException">The workspace is not paused, or the AI could not be continued.</exception>
    Task Resume(Guid id);

    /// <summary>
    /// Stops the work running in a workspace at once and keeps what it did: the workspace is <see cref="WorkspaceState.Stopping" /> meanwhile and <see cref="WorkspaceState.Ready" /> afterwards. A git action cannot be left half done, so stopping it undoes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is ready.</returns>
    /// <exception cref="RequestException">Nothing runs in the workspace, or it is already stopping or canceling.</exception>
    Task Stop(Guid id);

    /// <summary>
    /// Stops the AI's work in a workspace, working or paused, and reverts what it did by putting the files of the git repository back as they were when it started: the workspace is <see cref="WorkspaceState.Canceling" /> meanwhile and <see cref="WorkspaceState.Ready" /> once reverted.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is ready.</returns>
    /// <exception cref="RequestException">The AI is not working or paused in the workspace, the workspace is not a git repository, or git could not revert it.</exception>
    Task Cancel(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceControl(IWorkspaceActivity activity, IPromptService prompts, IScriptRunner scripts, IWorkspaceVersioning versioning, IWorkspaceSnapshots snapshots, IWorkspaceStore store, IWorkspaceManager workspaces, IEventHub events) : IWorkspaceControl
{
    private readonly SemaphoreSlim saving = new(1, 1);

    private CancellationToken stopping;

    private string stored = string.Empty;

    /// <inheritdoc />
    public async Task Restore(CancellationToken cancellation)
    {
        stopping = cancellation;
        WorkspaceCatalog catalog = await workspaces.List(cancellation);

        foreach (PausedWork work in await store.LoadPaused(cancellation))
        {
            if (catalog.Workspaces.Any(workspace => workspace.Id == work.WorkspaceId))
            {
                activity.RestorePaused(work.WorkspaceId);
                prompts.Restore(work.WorkspaceId, work.Prompts);

                if (work.Snapshot is { } snapshot)
                {
                    snapshots.Import(work.WorkspaceId, snapshot);
                }
            }
        }

        stored = JsonSerializer.Serialize(await Collect());
        events.Subscribe(remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.StateChanged)
            {
                _ = Save();
            }
        });
    }

    /// <inheritdoc />
    public async Task Pause(Guid id)
    {
        if (!activity.Pause(id))
        {
            throw new RequestException(activity.Get(id) == WorkspaceState.Ready ? "Nothing is running in this workspace." : activity.Get(id) == WorkspaceState.Working ? "The AI is not working in this workspace." : activity.Describe(id));
        }

        try
        {
            await prompts.Pause(id);
        }
        catch
        {
            if (prompts.IsBusy(id))
            {
                activity.Resume(id);
            }
            else
            {
                activity.Leave(id, WorkspaceState.Paused);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task Resume(Guid id)
    {
        if (activity.Get(id) != WorkspaceState.Paused)
        {
            throw new RequestException("The AI is not paused in this workspace.");
        }

        await prompts.Resume(id);
        activity.Resume(id);
    }

    /// <inheritdoc />
    public Task Stop(Guid id) => Halt(id, WorkspaceState.Stopping);

    /// <inheritdoc />
    public Task Cancel(Guid id)
    {
        WorkspaceState state = activity.Get(id);

        if (state is not (WorkspaceState.Working or WorkspaceState.Paused))
        {
            throw new RequestException(state == WorkspaceState.Ready ? "Nothing is running in this workspace." : state is WorkspaceState.Running or WorkspaceState.Managing ? "Only the AI's work can be cancelled. Stop it instead." : activity.Describe(id));
        }

        if (!snapshots.Has(id))
        {
            throw new RequestException("This workspace is not a git repository, so its work cannot be reverted. Stop it instead.");
        }

        return Halt(id, WorkspaceState.Canceling);
    }

    private async Task<List<PausedWork>> Collect()
    {
        List<PausedWork> paused = [];

        foreach (WorkspaceInfo workspace in (await workspaces.List(CancellationToken.None)).Workspaces.Where(workspace => activity.Get(workspace.Id) == WorkspaceState.Paused))
        {
            paused.Add(new PausedWork { WorkspaceId = workspace.Id, Snapshot = snapshots.Export(workspace.Id), Prompts = await prompts.Unanswered(workspace.Id) });
        }

        return paused;
    }

    private async Task Save()
    {
        await saving.WaitAsync(CancellationToken.None);

        try
        {
            if (stopping.IsCancellationRequested)
            {
                return;
            }

            List<PausedWork> paused = await Collect();
            string current = JsonSerializer.Serialize(paused);

            if (current != stored)
            {
                stored = current;
                await store.SavePaused(paused, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            saving.Release();
        }
    }

    private async Task Halt(Guid id, WorkspaceState halting)
    {
        WorkspaceState previous = activity.Halt(id, halting) ?? throw new RequestException(activity.Get(id) == WorkspaceState.Ready ? "Nothing is running in this workspace." : activity.Describe(id));

        try
        {
            switch (previous)
            {
                case WorkspaceState.Working or WorkspaceState.Paused:
                    await prompts.Interrupt(id);
                    break;
                case WorkspaceState.Running:
                    await scripts.StopAll(id);
                    break;
                case WorkspaceState.Managing:
                    Task cancelling = versioning.Cancel(id);
                    await prompts.Interrupt(id);
                    await cancelling;
                    break;
            }

            if (halting == WorkspaceState.Canceling && previous is WorkspaceState.Working or WorkspaceState.Paused)
            {
                await snapshots.Revert(id);
            }
        }
        finally
        {
            snapshots.Discard(id);
            activity.Leave(id, halting);
        }
    }
}
