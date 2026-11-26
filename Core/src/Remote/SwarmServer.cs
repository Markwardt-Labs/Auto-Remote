namespace Markwardt.AutoRemote;

/// <summary>
/// The swarm settings of one server.
/// </summary>
public sealed record SwarmServer
{
    /// <summary>
    /// Gets the name paired servers see, which is the machine's name until changed.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the fingerprint of the identity key of the server, which a person compares when pairing. Read only: ignored when setting.
    /// </summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the server takes tasks from the paired servers it also accepts work from.
    /// </summary>
    public bool AcceptDelegatedWork { get; init; }

    /// <summary>
    /// Gets the folder the workspaces of delegated tasks are made in; empty for one inside the data folder.
    /// </summary>
    public string DelegatedFolder { get; init; } = string.Empty;

    /// <summary>
    /// Gets the most tasks one delegated job may have. From 1 to 100.
    /// </summary>
    public int MaxTasksPerJob { get; init; } = 10;

    /// <summary>
    /// Gets the folder of the drive, on the machine of this server, which <see cref="RemoteCommand.SyncDrive" /> reads and writes.
    /// </summary>
    public string DriveFolder { get; init; } = string.Empty;
}
