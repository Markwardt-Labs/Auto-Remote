namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A paired server on the swarm screen. Switches and the delivery mode are sent as soon as they change; the address is sent when the screen closes or before the next action.
/// </summary>
internal sealed class SwarmPeerViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmPeerViewModel" /> class.
    /// </summary>
    /// <param name="peer">The server as it is.</param>
    /// <param name="changed">Sends the settings after a switch or the mode changed.</param>
    /// <param name="revoke">Asks to revoke the server.</param>
    public SwarmPeerViewModel(SwarmPeer peer, Func<SwarmPeerViewModel, Task> changed, Action<SwarmPeerViewModel> revoke)
    {
        this.changed = changed;
        stored = peer;
        address = peer.Address;
        RevokeCommand = new AsyncCommand(_ =>
        {
            revoke(this);
            return Task.CompletedTask;
        });
    }

    private readonly Func<SwarmPeerViewModel, Task> changed;

    private SwarmPeer stored;

    private string address;

    private bool isExpanded;

    /// <summary>
    /// Gets the identifier of the server.
    /// </summary>
    public string Id => stored.Id;

    /// <summary>
    /// Gets or sets a value indicating whether the settings of the server show.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    /// <summary>
    /// Gets the name of the server.
    /// </summary>
    public string Name => stored.Name;

    /// <summary>
    /// Gets the fingerprint of the server, to compare with the one it shows.
    /// </summary>
    public string Fingerprint => $"Fingerprint {stored.Fingerprint}";

    /// <summary>
    /// Gets or sets a value indicating whether tasks may be delegated to the server.
    /// </summary>
    public bool SendWork
    {
        get => stored.SendWork;
        set => Change(stored with { SendWork = value }, value != stored.SendWork);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the server's tasks are accepted.
    /// </summary>
    public bool AcceptWork
    {
        get => stored.AcceptWork;
        set => Change(stored with { AcceptWork = value }, value != stored.AcceptWork);
    }

    /// <summary>
    /// Gets the delivery modes to choose from.
    /// </summary>
    public IReadOnlyList<Choice<SwarmMode>> Modes { get; } =
    [
        new(SwarmMode.GitRemote, "Git remote"),
        new(SwarmMode.Direct, "Direct"),
        new(SwarmMode.AirGapped, "Air gapped")
    ];

    /// <summary>
    /// Gets or sets the delivery mode.
    /// </summary>
    public Choice<SwarmMode> SelectedMode
    {
        get => Modes.First(mode => mode.Value == stored.Mode);
        set => Change(stored with { Mode = value?.Value ?? stored.Mode }, value is not null && value.Value != stored.Mode);
    }

    /// <summary>
    /// Gets a value indicating whether the two connect over a network, which is when an address matters.
    /// </summary>
    public bool IsNetworked => stored.Mode != SwarmMode.AirGapped;

    /// <summary>
    /// Gets what the chosen delivery mode means.
    /// </summary>
    public string ModeText => stored.Mode switch
    {
        SwarmMode.GitRemote => "Code travels through the project's origin remote, which both must reach; tasks and results go over the connection.",
        SwarmMode.Direct => "Code, tasks and results go over the connection.",
        _ => "Everything is carried on a drive with Sync Drive; nothing goes over a network."
    };

    /// <summary>
    /// Gets or sets the address this server connects to, empty to wait for the other one to connect.
    /// </summary>
    public string Address
    {
        get => address;
        set => Set(ref address, value ?? string.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether the address was edited and not yet sent.
    /// </summary>
    public bool HasAddressEdit => address.Trim() != stored.Address;

    /// <summary>
    /// Gets or sets a value indicating whether tasks of the server run sandboxed here: without a shell and without files outside their workspace but what the server shares with them.
    /// </summary>
    public bool IsSandboxed
    {
        get => stored.Permissions.IsSandboxed;
        set => Change(stored with { Permissions = stored.Permissions with { IsSandboxed = value } }, value != stored.Permissions.IsSandboxed);
    }

    /// <summary>
    /// Gets or sets a value indicating whether tasks of the server may use the web.
    /// </summary>
    public bool IsWebAllowed
    {
        get => stored.Permissions.IsWebAllowed;
        set => Change(stored with { Permissions = stored.Permissions with { IsWebAllowed = value } }, value != stored.Permissions.IsWebAllowed);
    }

    /// <summary>
    /// Gets or sets a value indicating whether tasks of the server may generate images.
    /// </summary>
    public bool IsImageGenerationAllowed
    {
        get => stored.Permissions.IsImageGenerationAllowed;
        set => Change(stored with { Permissions = stored.Permissions with { IsImageGenerationAllowed = value } }, value != stored.Permissions.IsImageGenerationAllowed);
    }

    /// <summary>
    /// Gets what the server offers, from its last profile.
    /// </summary>
    public string Capabilities => stored.Capabilities.Count == 0 ? "No models known yet." : string.Join(", ", stored.Capabilities.Select(capability => $"{capability.Capability}: {capability.Model}"));

    /// <summary>
    /// Gets how the server is: connected or when it was last heard from, whether it takes work from here, how old that is and any problem.
    /// </summary>
    public string Status
    {
        get
        {
            List<string> parts = [stored.IsConnected ? "Connected" : stored.LastSeen is { } seen ? $"Last heard from {seen.ToStamp(DateTimeOffset.UtcNow)}" : "Never heard from"];

            if (stored.ProfileAt is { } at)
            {
                parts.Add(stored.AcceptsOurWork ? $"accepts your work, {stored.FreeSlots} free" : "does not accept your work");

                if (stored.Mode == SwarmMode.AirGapped)
                {
                    parts.Add($"as of {at.ToStamp(DateTimeOffset.UtcNow)}");
                }
            }

            return string.Join(" - ", parts);
        }
    }

    /// <summary>
    /// Gets why the last connection or delivery failed, or <see langword="null" />.
    /// </summary>
    public string? Problem => stored.Problem;

    /// <summary>
    /// Gets a value indicating whether the last connection or delivery failed.
    /// </summary>
    public bool HasProblem => stored.Problem is not null;

    /// <summary>
    /// Gets the command that asks to revoke the server.
    /// </summary>
    public AsyncCommand RevokeCommand { get; }

    /// <summary>
    /// Takes what the server now says about this paired server, keeping an address being edited.
    /// </summary>
    /// <param name="peer">The server as it is.</param>
    public void Show(SwarmPeer peer)
    {
        bool editing = address != stored.Address;
        stored = peer;

        if (!editing)
        {
            address = peer.Address;
        }

        Raise(null);
    }

    /// <summary>
    /// Gets the settings to send.
    /// </summary>
    /// <returns>The settings.</returns>
    public SwarmPeer Settings() => stored with { Address = address.Trim() };

    private void Change(SwarmPeer next, bool differs)
    {
        if (!differs)
        {
            return;
        }

        stored = next;
        Raise(null);
        _ = changed(this);
    }
}
