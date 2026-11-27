namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How a change of the Ollama service ended.
/// </summary>
internal enum RootOutcome
{
    /// <summary>
    /// The change was made.
    /// </summary>
    Done,

    /// <summary>
    /// <c>sudo</c> did not accept the password.
    /// </summary>
    WrongPassword,

    /// <summary>
    /// The user may not use <c>sudo</c>, or <c>sudo</c> is not installed.
    /// </summary>
    NotAllowed,

    /// <summary>
    /// The change failed for another reason, which the result says.
    /// </summary>
    Failed
}

/// <summary>
/// What a change of the Ollama service came to.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">The reason for an outcome that is not <see cref="RootOutcome.Done" />, or <see langword="null" />.</param>
internal sealed record RootResult(RootOutcome Outcome, string? Message = null);

/// <summary>
/// The <c>ollama.service</c> unit of a Linux machine with systemd, which can only be changed with root rights: a drop-in file that sets the variables of the optimized context is written (or removed) and the service restarted through <c>sudo</c>, with the password of the server's user given on its standard input when passwordless sudo does not work.
/// </summary>
internal interface IOllamaServiceUnit
{
    /// <summary>
    /// Tells whether this is a Linux machine with systemd and an <c>ollama.service</c> unit.
    /// </summary>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns>Whether the unit can be configured.</returns>
    Task<bool> IsAvailable(CancellationToken cancellation = default);

    /// <summary>
    /// Tells whether changing the unit needs a password, which is when passwordless <c>sudo</c> does not work.
    /// </summary>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns>Whether a password is needed.</returns>
    Task<bool> NeedsPassword(CancellationToken cancellation = default);

    /// <summary>
    /// Tells whether the drop-in is as <paramref name="enabled" /> wants it: present with the variables, or absent.
    /// </summary>
    /// <param name="enabled">Whether the optimized context is wanted.</param>
    /// <returns>Whether nothing has to change.</returns>
    bool Matches(bool enabled);

    /// <summary>
    /// Writes or removes the drop-in, reloads systemd and restarts the service, all as root in one <c>sudo</c> call.
    /// </summary>
    /// <param name="enabled">Whether to write the drop-in (<see langword="true" />) or remove it.</param>
    /// <param name="password">The password for <c>sudo</c>, or <see langword="null" /> to run it without one. It is written to the standard input of <c>sudo</c> and nowhere else.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>How it ended; it never throws for a failed change.</returns>
    Task<RootResult> Apply(bool enabled, string? password, CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="run">Runs a process with an optional text for its standard input and gives its exit code, standard output and standard error, or <see langword="null" /> to run it for real; it throws <see cref="Win32Exception" /> when the program does not exist.</param>
/// <param name="exists">Tells whether a folder exists, or <see langword="null" /> to ask the file system.</param>
/// <param name="read">Reads a text file, or gives <see langword="null" /> when it does not exist; <see langword="null" /> here reads the real file.</param>
internal sealed class OllamaServiceUnit(Func<ProcessStartInfo, string?, CancellationToken, Task<(int Code, string Output, string Errors)>>? run = null, Func<string, bool>? exists = null, Func<string, string?>? read = null) : IOllamaServiceUnit
{
    private static async Task<(int Code, string Output, string Errors)> Execute(ProcessStartInfo info, string? input, CancellationToken cancellation)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info) ?? throw new Win32Exception("The process could not be started.");

        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellation);

            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellation);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation);
            return (process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
    }

    private readonly Func<ProcessStartInfo, string?, CancellationToken, Task<(int Code, string Output, string Errors)>> execute = run ?? Execute;

    private readonly Func<string, bool> folderExists = exists ?? Directory.Exists;

    private readonly Func<string, string?> readFile = read ?? (path => File.Exists(path) ? File.ReadAllText(path) : null);

    private readonly string install = "install -D -m 0644 -o root -g root \"$1\" \"$2\" && systemctl daemon-reload && systemctl restart \"$3\"";

    private readonly string remove = "rm -f \"$2\" && systemctl daemon-reload && systemctl restart \"$3\"";

    /// <summary>
    /// Gets the name of the unit.
    /// </summary>
    internal string Unit { get; init; } = "ollama.service";

    /// <summary>
    /// Gets the path of the drop-in; it sorts after the files a person makes, so what Auto Remote asks for wins.
    /// </summary>
    internal string DropIn { get; init; } = "/etc/systemd/system/ollama.service.d/zz-auto-remote.conf";

    /// <summary>
    /// Gets how long the root change may take.
    /// </summary>
    internal TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Gets the text of the drop-in.
    /// </summary>
    internal string Content { get; } = "# Written by Auto Remote (Optimize context); removing it restores the service as it was.\n[Service]\nEnvironment=\"OLLAMA_KV_CACHE_TYPE=q4_0\"\nEnvironment=\"OLLAMA_FLASH_ATTENTION=1\"\nEnvironment=\"OLLAMA_LOAD_TIMEOUT=0\"\n";

    /// <inheritdoc />
    public async Task<bool> IsAvailable(CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsLinux() || !folderExists("/run/systemd/system"))
        {
            return false;
        }

        try
        {
            return (await execute(Command("systemctl", "cat", Unit), null, cancellation)).Code == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> NeedsPassword(CancellationToken cancellation = default)
    {
        try
        {
            return (await execute(Command("sudo", "-n", "true"), null, cancellation)).Code != 0;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public bool Matches(bool enabled) => enabled ? readFile(DropIn) == Content : readFile(DropIn) is null;

    /// <inheritdoc />
    public async Task<RootResult> Apply(bool enabled, string? password, CancellationToken cancellation = default)
    {
        string? staged = enabled ? Path.Combine(Path.GetTempPath(), $"auto-remote-{Guid.NewGuid():N}.conf") : null;

        try
        {
            if (staged is not null)
            {
                await File.WriteAllTextAsync(staged, Content, cancellation);
            }

            ProcessStartInfo info = password is null ? Command("sudo", "-n") : Command("sudo", "-S", "-k", "-p", string.Empty);
            info.ArgumentList.Add("sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(enabled ? install : remove);
            info.ArgumentList.Add("sh");
            info.ArgumentList.Add(staged ?? string.Empty);
            info.ArgumentList.Add(DropIn);
            info.ArgumentList.Add(Unit);
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(Limit);
            (int code, _, string errors) = await execute(info, password is null ? null : $"{password}\n", limit.Token);
            return code == 0 ? new RootResult(RootOutcome.Done) : Classify(errors);
        }
        catch (Win32Exception)
        {
            return new RootResult(RootOutcome.NotAllowed, "sudo is not installed on the server.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new RootResult(RootOutcome.Failed, "Restarting the Ollama service took too long.");
        }
        finally
        {
            if (staged is not null)
            {
                File.Delete(staged);
            }
        }
    }

    private ProcessStartInfo Command(string program, params string[] arguments)
    {
        ProcessStartInfo info = new(program) { UseShellExecute = false };
        info.Environment["LC_ALL"] = "C";

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private RootResult Classify(string errors)
    {
        string text = errors.Trim();

        if (text.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) || text.Contains("no password was provided", StringComparison.OrdinalIgnoreCase) || text.Contains("try again", StringComparison.OrdinalIgnoreCase) || text.Contains("a password is required", StringComparison.OrdinalIgnoreCase))
        {
            return new RootResult(RootOutcome.WrongPassword);
        }

        if (text.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) || text.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase) || text.Contains("is not allowed to", StringComparison.OrdinalIgnoreCase))
        {
            return new RootResult(RootOutcome.NotAllowed, "This user may not use sudo on the server.");
        }

        string first = text.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;
        return new RootResult(RootOutcome.Failed, first.Length > 0 ? first : "Configuring the Ollama service failed.");
    }
}
