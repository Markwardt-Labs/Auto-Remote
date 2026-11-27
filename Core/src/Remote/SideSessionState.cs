namespace Markwardt.AutoRemote;

/// <summary>
/// Where a side session is in its life. Whether it is working is its <see cref="WorkspaceState" />, like any workspace's.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SideSessionState
{
    /// <summary>
    /// Its files, its AI session and its setup are being made. Nothing can be done with it yet.
    /// </summary>
    Creating,

    /// <summary>
    /// It is used like any workspace.
    /// </summary>
    Ready,

    /// <summary>
    /// It is being brought into main. It cannot be used until the integration finishes, which removes it, or is cancelled.
    /// </summary>
    Integrating,

    /// <summary>
    /// It is being removed.
    /// </summary>
    Deleting
}
