namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The server's settings, loaded from the JSON config file.
/// </summary>
internal sealed record ServerConfig
{
    /// <summary>
    /// Gets the port every client connects to, over secure WebSocket (52679 unless set).
    /// </summary>
    public int Port { get; init; } = 52679;

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
    /// Gets the .NET command that runs scripts with <c>run --file</c>.
    /// </summary>
    public string DotnetCommand { get; init; } = "dotnet";

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
    /// Gets the Whisper model that turns speech into text inside the server for voice input: a model name such as <c>small.en</c>, downloaded into the <c>models</c> folder of the data folder the first time, or the path of an existing <c>ggml</c> model file. Empty turns the built-in voice input off, unless <see cref="SpeechCommand" /> is set.
    /// </summary>
    public string? SpeechModel { get; init; } = "small.en";

    /// <summary>
    /// Gets the executable that turns speech into text for voice input instead of the built-in <see cref="SpeechModel" />; when empty, the built-in one is used.
    /// </summary>
    public string? SpeechCommand { get; init; }

    /// <summary>
    /// Gets the arguments of the speech command, in which <c>{file}</c> stands for the path of the recorded WAV file; the command prints the text to its standard output.
    /// </summary>
    public IReadOnlyList<string> SpeechArguments { get; init; } = [];

    /// <summary>
    /// Gets the extra arguments passed to every Codex run, after the always present <c>--dangerously-bypass-approvals-and-sandbox</c>.
    /// </summary>
    public IReadOnlyList<string> CodexArguments { get; init; } = [];
}
