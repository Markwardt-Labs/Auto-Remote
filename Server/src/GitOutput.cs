namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What a git process printed and how it ended.
/// </summary>
/// <param name="ExitCode">The exit code; zero means success.</param>
/// <param name="Output">The standard output.</param>
/// <param name="Error">The standard error.</param>
internal sealed record GitOutput(int ExitCode, string Output, string Error);
