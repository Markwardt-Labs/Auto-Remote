namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Ends the work running in a workspace on request: the AI's work can be stopped (keeping what it did) or cancelled (reverting it), while a script or a git action can only be stopped.
/// </summary>
internal interface IWorkspaceControl
{
    /// <summary>
    /// Stops the work running in a workspace at once, cancels its loop if one runs, and keeps what it did: the workspace is <see cref="WorkspaceState.Stopping" /> meanwhile and <see cref="WorkspaceState.Ready" /> afterwards. A git action cannot be left half done, so stopping it undoes it.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is ready.</returns>
    /// <exception cref="RequestException">Nothing runs in the workspace, or it is already stopping or canceling.</exception>
    Task Stop(Guid id);

    /// <summary>
    /// Stops the AI's work in a workspace and reverts what it did by putting the files of the git repository back as they were when it started: the workspace is <see cref="WorkspaceState.Canceling" /> meanwhile and <see cref="WorkspaceState.Ready" /> once reverted.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is ready.</returns>
    /// <exception cref="RequestException">The AI is not working in the workspace, the workspace is not a git repository, or git could not revert it.</exception>
    Task Cancel(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceControl(IWorkspaceActivity activity, IPromptService prompts, IWorkspaceLoops loops, IScriptRunner scripts, IWorkspaceVersioning versioning, IWorkspaceSnapshots snapshots) : IWorkspaceControl
{
    /// <inheritdoc />
    public Task Stop(Guid id) => Halt(id, WorkspaceState.Stopping);

    /// <inheritdoc />
    public Task Cancel(Guid id)
    {
        WorkspaceState state = activity.Get(id);

        if (state != WorkspaceState.Working)
        {
            throw new RequestException(state == WorkspaceState.Ready ? "Nothing is running in this workspace." : state is WorkspaceState.Running or WorkspaceState.Managing ? "Only the AI's work can be cancelled. Stop it instead." : activity.Describe(id));
        }

        if (!snapshots.Has(id))
        {
            throw new RequestException("This workspace is not a git repository, so its work cannot be reverted. Stop it instead.");
        }

        return Halt(id, WorkspaceState.Canceling);
    }

    private async Task Halt(Guid id, WorkspaceState halting)
    {
        WorkspaceState previous = activity.Halt(id, halting) ?? throw new RequestException(activity.Get(id) == WorkspaceState.Ready ? "Nothing is running in this workspace." : activity.Describe(id));

        try
        {
            await loops.Cancel(id);

            switch (previous)
            {
                case WorkspaceState.Working:
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

            if (halting == WorkspaceState.Canceling && previous == WorkspaceState.Working)
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
