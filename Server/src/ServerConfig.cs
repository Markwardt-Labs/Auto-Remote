namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The server's settings, loaded from the JSON config file.
/// </summary>
internal sealed record ServerConfig
{
    /// <summary>
    /// Gets the TCP port the mobile and desktop clients connect to (52678 unless set).
    /// </summary>
    public int Port { get; init; } = 52678;

    /// <summary>
    /// Gets the WebSocket port the web client connects to (52679 unless set).
    /// </summary>
    public int WebPort { get; init; } = 52679;

    /// <summary>
    /// Gets the key a client must supply to connect.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the folder holding everything the server stores: the workspaces file, the <c>history</c> folder and the generated certificate. Created when missing; the <c>AutoRemote</c> folder of the server user's application data unless set.
    /// </summary>
    public string DataFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoRemote");

    /// <summary>
    /// Gets the existing folder that browsing for a new workspace's folder starts in; the server user's home folder unless set.
    /// </summary>
    public string DefaultFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Gets the Claude Code executable to run for prompts.
    /// </summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>
    /// Gets the PFX certificate file presented to clients; when empty, a self-signed certificate is created in the data folder.
    /// </summary>
    public string? CertificateFile { get; init; }

    /// <summary>
    /// Gets the password of the PFX certificate, if it has one.
    /// </summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// Gets the extra arguments passed to every Claude Code run, after the always present <c>--dangerously-skip-permissions</c>.
    /// </summary>
    public IReadOnlyList<string> ClaudeArguments { get; init; } = [];

    /// <summary>
    /// Gets the Codex executable to run for prompts.
    /// </summary>
    public string CodexCommand { get; init; } = "codex";

    /// <summary>
    /// Gets the extra arguments passed to every Codex run, after the always present <c>--dangerously-bypass-approvals-and-sandbox</c>.
    /// </summary>
    public IReadOnlyList<string> CodexArguments { get; init; } = [];
}
