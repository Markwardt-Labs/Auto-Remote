namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tools that let a workspace's AI delegate: listing the workers, delegating, following, cancelling, reviewing and applying its tasks. Delegating is only offered while the workspace allows it; the tools for the tasks stay while there are results to take. An AI only ever sees and touches the tasks of its own workspace, and the AI of a delegated task gets none of them.
/// </summary>
/// <param name="book">The delegated tasks.</param>
/// <param name="coordinator">Delegates tasks and takes their results.</param>
/// <param name="worker">Runs tasks delegated to this server.</param>
/// <param name="workspaces">Knows the workspaces and what their AI may do.</param>
internal sealed class DelegationTools(ISwarmBook book, ISwarmCoordinator coordinator, ISwarmWorker worker, IWorkspaceManager workspaces) : ISessionTools
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<PromptTool>> made = new();

    private readonly HashSet<string> delegating = ["list_workers", "delegate_tasks"];

    /// <inheritdoc />
    public async Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation)
    {
        HashSet<string> wanted = [];

        if (!worker.IsDelegated(workspace.Id))
        {
            bool pending = await book.Read(document => document.Outgoing.Any(task => task.WorkspaceId == workspace.Id && !task.IsManual && (task.State != SwarmTaskState.Done || task.ResultCommit is not null && !task.IsApplied)));

            if (workspace.AllowDelegation)
            {
                wanted.UnionWith(delegating);
            }

            if (workspace.AllowDelegation || pending)
            {
                wanted.UnionWith(["task_status", "cancel_task", "review_task", "apply_task"]);
            }
        }

        // The same tools are added again, so a session that already serves them does not restart its process.
        IReadOnlyList<PromptTool> tools = made.GetOrAdd(workspace.Id, Make);
        IList<PromptTool> offered = session.Transient.Tools;

        foreach (PromptTool tool in tools)
        {
            bool present = offered.Any(other => other.Name == tool.Name);

            if (wanted.Contains(tool.Name) && !present)
            {
                offered.Add(tool);
            }
            else if (!wanted.Contains(tool.Name) && present)
            {
                offered.Remove(offered.First(other => other.Name == tool.Name));
            }
        }
    }

    /// <inheritdoc />
    public bool Keeps(Guid workspaceId) => worker.IsDelegated(workspaceId);

    private IReadOnlyList<PromptTool> Make(Guid workspace) =>
    [
        PromptTool.Create<DelegationNoInput, string>("list_workers", "Lists the other Auto Remote servers you can delegate tasks to: their capabilities (which local models serve Fast, Balanced, Strong and Max), how work reaches them, what tasks may do there, and how recent that information is. Call it before delegating.", (_, cancellation) => Workers(workspace)),
        PromptTool.Create<DelegationRequest, string>("delegate_tasks", "Delegates tasks to other servers as one job. Each worker starts from the current files of this workspace, uncommitted changes included but ignored files not, sees nothing of this conversation and cannot ask anything, so each task's instructions must be complete on their own, small and checkable; say in expectedResult how to check it. Workers without shell permission cannot build or run tests. Leave worker out to let any networked worker take a task; air gapped workers must be named, and their results can take days. A task that builds on others (afterTasksInThisCall, positions from 1, or afterTaskIds) starts with their changes. After delegating, END YOUR TURN: do not wait or poll. You are prompted when every task of the job has ended.", (input, cancellation) => Delegate(workspace, input, cancellation)),
        PromptTool.Create<DelegationNoInput, string>("task_status", "Lists the tasks you delegated from this workspace and where each stands.", (_, cancellation) => Status(workspace)),
        PromptTool.Create<DelegationTaskReference, string>("cancel_task", "Cancels a delegated task that has not ended.", async (input, cancellation) =>
        {
            await coordinator.Cancel(await Own(workspace, input.TaskId));
            return "Cancelled.";
        }),
        PromptTool.Create<DelegationTaskReference, string>("review_task", "Shows the result of a delegated task: the worker's report and its changes (a summary per file and the patch; give file to see one file's patch when it is long). Check the changes yourself rather than trusting the report.", (input, cancellation) => Review(workspace, input, cancellation)),
        PromptTool.Create<DelegationTaskReference, string>("apply_task", "Applies the changes of a finished delegated task to the files of this workspace, merged with what changed meanwhile, without committing. Files left with conflict markers must be resolved by editing them.", async (input, cancellation) =>
        {
            SwarmApplyResult applied = await coordinator.Apply(await Own(workspace, input.TaskId), cancellation);
            return applied.Conflicts.Count > 0 ? $"Applied with conflicts: resolve the conflict markers in {string.Join(", ", applied.Conflicts)}." : $"Applied the changes to {applied.Files} files.";
        })
    ];

    private async Task<string> Workers(Guid workspace)
    {
        await Allowed(workspace);
        (List<string> lines, int most) = await book.Read(document => (document.Peers.Where(peer => peer.SendWork).Select(Describe).ToList(), document.Server.MaxTasksPerJob));
        return lines.Count == 0
            ? "No paired server is set to receive work, so nothing can be delegated. The user sets that up in the Swarm settings."
            : $"{string.Join("\n", lines)}\n\nA job may have at most {most} tasks.";
    }

    private string Describe(SwarmPeerRecord peer)
    {
        string delivery = peer.Mode switch
        {
            SwarmMode.AirGapped => "air gapped: tasks and results are carried on a drive, so name it, group independent work into one job and avoid chains of dependent tasks",
            SwarmMode.GitRemote => "network, code through the project's git remote",
            _ => "network"
        };

        if (peer.Profile is not { } profile)
        {
            return $"- {peer.Name} (id {peer.Identity.Id}): {delivery}. Nothing is known about it yet.";
        }

        string capabilities = profile.Capabilities.Count > 0 ? string.Join(", ", profile.Capabilities.Select(capability => $"{capability.Capability} ({capability.Model}{(capability.Context is { } context ? $", {context} tokens" : string.Empty)})")) : "no models";
        List<string> permissions = [];

        if (profile.Permissions.IsShellAllowed)
        {
            permissions.Add("run shell commands");
        }

        if (profile.Permissions.IsWebAllowed)
        {
            permissions.Add("use the web");
        }

        if (profile.Permissions.IsImageGenerationAllowed)
        {
            permissions.Add("generate images");
        }

        string age = peer.ProfileAt is { } at ? $"{(int)Math.Max(0, (DateTimeOffset.UtcNow - at).TotalMinutes)} minutes old" : "of unknown age";
        return $"- {peer.Name} (id {peer.Identity.Id}): {delivery}. Capabilities: {capabilities}. Tasks may {(permissions.Count > 0 ? string.Join(", ", permissions) : "only read and edit files")}. {(profile.AcceptsWork ? $"Free now: {profile.FreeSlots}" : "Does not accept work now")}. This information is {age}.";
    }

    private async Task<string> Delegate(Guid workspace, DelegationRequest input, CancellationToken cancellation)
    {
        await Allowed(workspace);

        if (input.Tasks is not { Count: > 0 })
        {
            throw new RequestException("Give at least one task.");
        }

        Dictionary<string, string> workers = await book.Read(document => document.Peers.ToDictionary(peer => peer.Identity.Id, peer => peer.Name));
        List<SwarmPlannedTask> planned = [];

        foreach (DelegationRequestedTask task in input.Tasks)
        {
            AiCapability capability = AiCapability.Balanced;

            if (!string.IsNullOrWhiteSpace(task.Capability) && !Enum.TryParse(task.Capability, true, out capability))
            {
                throw new RequestException($"'{task.Capability}' is not a capability; use Fast, Balanced, Strong or Max.");
            }

            string? peer = null;

            if (!string.IsNullOrWhiteSpace(task.Worker))
            {
                List<string> matches = [.. workers.Where(worker => worker.Key == task.Worker || string.Equals(worker.Value, task.Worker, StringComparison.OrdinalIgnoreCase)).Select(worker => worker.Key)];
                peer = matches.Count == 1 ? matches[0] : throw new RequestException(matches.Count == 0 ? $"There is no worker '{task.Worker}'; see list_workers." : $"Several workers are named '{task.Worker}'; use the identifier.");
            }

            List<Guid> dependencies = [];

            foreach (string id in task.AfterTaskIds ?? [])
            {
                dependencies.Add(await Own(workspace, id));
            }

            planned.Add(new SwarmPlannedTask(new SwarmTaskRequest { Title = task.Title ?? string.Empty, Instructions = task.Instructions ?? string.Empty, ExpectedResult = task.ExpectedResult, Capability = capability, PeerId = peer, DependsOn = dependencies }, [.. (task.AfterTasksInThisCall ?? []).Select(position => position - 1)]));
        }

        IReadOnlyList<Guid> ids = await coordinator.Delegate(workspace, planned, false, cancellation);
        return $"Delegated {ids.Count} tasks:\n{string.Join("\n", ids.Select((id, index) => $"{index + 1}. {planned[index].Request.Title}: {id:N}"))}\n\nNow end your turn. You will be prompted when they have ended.";
    }

    private async Task<string> Status(Guid workspace)
    {
        List<string> lines = await book.Read(document => document.Outgoing.Where(task => task.WorkspaceId == workspace).OrderBy(task => task.CreatedAt).Select(task => $"- {task.Id:N} \"{task.Title}\": {task.State}{(task.PeerName.Length > 0 ? $" on {task.PeerName}" : string.Empty)}{(task.Error is { } error ? $" ({error})" : string.Empty)}{(task.IsApplied ? ", applied" : string.Empty)}{(task.IsManual ? ", delegated by the user" : string.Empty)}").ToList());
        return lines.Count > 0 ? string.Join("\n", lines) : "Nothing was delegated from this workspace.";
    }

    private async Task<string> Review(Guid workspace, DelegationTaskReference input, CancellationToken cancellation)
    {
        Guid id = await Own(workspace, input.TaskId);
        (string title, SwarmTaskState state, string? report, string? error) = await book.Read(document =>
        {
            SwarmOutgoing task = document.Outgoing.First(task => task.Id == id);
            return (task.Title, task.State, task.Report, task.Error);
        });
        string changes = state == SwarmTaskState.Done ? await coordinator.Changes(id, string.IsNullOrWhiteSpace(input.File) ? null : input.File, cancellation) : "There are no changes to show.";
        return $"Task \"{title}\": {state}{(error is null ? string.Empty : $" ({error})")}\n\nReport:\n{report ?? "(none)"}\n\nChanges:\n{changes}";
    }

    private async Task Allowed(Guid workspace)
    {
        if (!(await workspaces.Get(workspace, CancellationToken.None)).AllowDelegation)
        {
            throw new RequestException("Delegating is turned off for this workspace.");
        }
    }

    private async Task<Guid> Own(Guid workspace, string? taskId)
    {
        if (!Guid.TryParse(taskId, out Guid id) || !await book.Read(document => document.Outgoing.Any(task => task.Id == id && task.WorkspaceId == workspace)))
        {
            throw new RequestException("This workspace delegated no task with that identifier; see task_status.");
        }

        return id;
    }
}
