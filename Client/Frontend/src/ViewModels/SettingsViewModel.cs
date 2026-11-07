namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The settings of one workspace's AI session: how capable it is, how hard it thinks, what it may do and its standing instructions. The provider is fixed when the workspace is created and only shown. Edits are pending and are sent to the server together by <see cref="Apply" /> when the settings are closed; the server reads the values from the session and writes them back to it, so nothing is kept here beyond what is on screen.
/// </summary>
internal sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace.</param>
    public SettingsViewModel(IServerConnection connection, WorkspaceInfo workspace)
    {
        this.connection = connection;
        ProviderName = workspace.Service.Label();
        workspaceId = workspace.Id;
        _ = Load();
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private WorkspaceSettings current = new();

    private WorkspaceSettings pending = new();

    private IReadOnlyList<DetailLine> details = [];

    private int edits;

    /// <summary>
    /// Gets the name of the provider the workspace runs on, which never changes.
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    /// Gets the capabilities to choose from.
    /// </summary>
    public IReadOnlyList<Choice<AiCapability>> Capabilities { get; } = [.. Enum.GetValues<AiCapability>().Select(value => new Choice<AiCapability>(value, value.ToString()))];

    /// <summary>
    /// Gets the efforts to choose from.
    /// </summary>
    public IReadOnlyList<Choice<AiEffort>> Efforts { get; } = [.. Enum.GetValues<AiEffort>().Select(value => new Choice<AiEffort>(value, value.ToString()))];

    /// <summary>
    /// Gets what the session says about how it runs (the model it chose and the settings it runs that model with).
    /// </summary>
    public IReadOnlyList<DetailLine> Details => details;

    /// <summary>
    /// Gets a value indicating whether the session says anything about how it runs.
    /// </summary>
    public bool HasDetails => details.Count > 0;

    /// <summary>
    /// Gets a value indicating whether something was changed that <see cref="Apply" /> has not sent yet.
    /// </summary>
    public bool HasPending => Differs(pending, current);

    /// <summary>
    /// Gets or sets the capability, which decides the model.
    /// </summary>
    public Choice<AiCapability> SelectedCapability
    {
        get => Capabilities.First(choice => choice.Value == pending.Capability);
        set
        {
            if (value is not null)
            {
                Edit(pending with { Capability = value.Value });
            }
        }
    }

    /// <summary>
    /// Gets or sets the effort.
    /// </summary>
    public Choice<AiEffort> SelectedEffort
    {
        get => Efforts.First(choice => choice.Value == pending.Effort);
        set
        {
            if (value is not null)
            {
                Edit(pending with { Effort = value.Value });
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the AI may run shell commands.
    /// </summary>
    public bool IsShellAllowed
    {
        get => pending.IsShellAllowed;
        set => Edit(pending with { IsShellAllowed = value });
    }

    /// <summary>
    /// Gets or sets whether the AI's file tools may use any path on the server's machine and not only the ones inside the workspace.
    /// </summary>
    public bool IsSystemAccessAllowed
    {
        get => pending.IsSystemAccessAllowed;
        set => Edit(pending with { IsSystemAccessAllowed = value });
    }

    /// <summary>
    /// Gets or sets whether the AI may use the web.
    /// </summary>
    public bool IsWebAllowed
    {
        get => pending.IsWebAllowed;
        set => Edit(pending with { IsWebAllowed = value });
    }

    /// <summary>
    /// Gets or sets whether the AI may generate images.
    /// </summary>
    public bool IsImageGenerationAllowed
    {
        get => pending.IsImageGenerationAllowed;
        set => Edit(pending with { IsImageGenerationAllowed = value });
    }

    /// <summary>
    /// Gets or sets the standing instructions the AI is given.
    /// </summary>
    public string Instructions
    {
        get => pending.Instructions ?? string.Empty;
        set => Edit(pending with { Instructions = value });
    }

    /// <summary>
    /// Reads the settings from the server again, unless there are edits that were not applied (they stay).
    /// </summary>
    /// <returns>A task that completes once the read has finished.</returns>
    public Task Reload() => HasPending ? Task.CompletedTask : Load();

    /// <summary>
    /// Sends the edits to the server, which gives them to the session from its next prompt on.
    /// </summary>
    /// <returns><see langword="true" /> when there was nothing to send or the server took the edits; <see langword="false" /> when it refused, which <see cref="ViewModelBase.Error" /> says, and the edits stay pending.</returns>
    public async Task<bool> Apply()
    {
        if (!HasPending)
        {
            return true;
        }

        WorkspaceSettings? saved = null;
        WorkspaceSettings wanted = pending with { Instructions = (pending.Instructions ?? string.Empty).Trim() };

        if (await Attempt(async () => saved = await connection.SetWorkspaceSettings(workspaceId, wanted, CancellationToken.None)) && saved is not null)
        {
            Show(saved);
            return true;
        }

        return false;
    }

    private bool Differs(WorkspaceSettings first, WorkspaceSettings second)
        => (first.Capability, first.Effort, first.IsShellAllowed, first.IsSystemAccessAllowed, first.IsWebAllowed, first.IsImageGenerationAllowed, (first.Instructions ?? string.Empty).Trim()) != (second.Capability, second.Effort, second.IsShellAllowed, second.IsSystemAccessAllowed, second.IsWebAllowed, second.IsImageGenerationAllowed, (second.Instructions ?? string.Empty).Trim());

    private void Edit(WorkspaceSettings edited)
    {
        edits++;
        pending = edited;
        Raise(nameof(HasPending));
    }

    private async Task Load()
    {
        WorkspaceSettings? read = null;
        int before = edits;

        if (await Attempt(async () => read = await connection.GetWorkspaceSettings(workspaceId, CancellationToken.None)) && read is not null)
        {
            if (edits == before)
            {
                Show(read);
                return;
            }

            current = read;
            details = [.. read.Descriptors.Select(pair => new DetailLine(pair.Key, pair.Value))];
            Raise(nameof(Details));
            Raise(nameof(HasDetails));
            Raise(nameof(HasPending));
        }
    }

    private void Show(WorkspaceSettings settings)
    {
        current = settings;
        pending = settings;
        details = [.. settings.Descriptors.Select(pair => new DetailLine(pair.Key, pair.Value))];
        Raise(nameof(Details));
        Raise(nameof(HasDetails));
        Raise(nameof(HasPending));
        Raise(nameof(SelectedCapability));
        Raise(nameof(SelectedEffort));
        Raise(nameof(IsShellAllowed));
        Raise(nameof(IsSystemAccessAllowed));
        Raise(nameof(IsWebAllowed));
        Raise(nameof(IsImageGenerationAllowed));
        Raise(nameof(Instructions));
    }
}
