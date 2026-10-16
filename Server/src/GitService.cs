namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Runs git in workspace folders and reads its output; this is the only place git is ever run.
/// </summary>
internal interface IGitService
{
    /// <summary>
    /// Reads the git state of a folder.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The state; <see cref="GitStatus.IsRepo" /> is false when the folder is not the root of a repository or git is not installed.</returns>
    Task<GitStatus> Status(string folder, CancellationToken cancellation);

    /// <summary>
    /// Lists the paths that differ from the head commit, and the ones git ignores.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The paths, ordered by path; empty when the folder is not a repository.</returns>
    Task<IReadOnlyList<GitChange>> Changes(string folder, CancellationToken cancellation);

    /// <summary>
    /// Reads a chunk of the history, newest commit first, across all branches and tags.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="skip">How many of the newest commits to skip.</param>
    /// <param name="limit">The most commits to read.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The chunk; empty when the repository has no commits.</returns>
    /// <exception cref="RequestException">The folder is not a repository.</exception>
    Task<GitHistory> History(string folder, int skip, int limit, CancellationToken cancellation);

    /// <summary>
    /// Lists the paths a commit changed relative to its first parent.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="commit">The commit hash.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The paths, ordered by path.</returns>
    /// <exception cref="RequestException">The folder is not a repository or the commit is unknown.</exception>
    Task<IReadOnlyList<GitChange>> CommitChanges(string folder, string commit, CancellationToken cancellation);

    /// <summary>
    /// Reads the diff of one file.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="path">The file, relative to the workspace folder.</param>
    /// <param name="commit">The commit to diff against its first parent, or <see langword="null" /> to diff the working tree against the head.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The whole file with its changes marked.</returns>
    /// <exception cref="RequestException">The folder is not a repository, the path is not inside it, or the commit is unknown.</exception>
    Task<GitDiff> Diff(string folder, string path, string? commit, CancellationToken cancellation);

    /// <summary>
    /// Stages every change and commits it, then pushes the branch when there is a remote; a failing push is ignored.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="message">The commit message.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The folder is not a repository, there is nothing to commit, or git refused.</exception>
    Task Commit(string folder, string message, CancellationToken cancellation);

    /// <summary>
    /// Discards every change that is not committed, new files included.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The folder is not a repository or git refused.</exception>
    Task Reset(string folder, CancellationToken cancellation);

    /// <summary>
    /// Creates a branch at the head and checks it out.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="name">The branch name.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The folder is not a repository, the name is invalid or taken, or git refused.</exception>
    Task CreateBranch(string folder, string name, CancellationToken cancellation);

    /// <summary>
    /// Creates a tag at the head.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="name">The tag name.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The folder is not a repository, there is no commit to tag, the name is invalid or taken, or git refused.</exception>
    Task CreateTag(string folder, string name, CancellationToken cancellation);

    /// <summary>
    /// Sets the <c>origin</c> remote, creating the repository first when the folder is not one.
    /// </summary>
    /// <param name="folder">The workspace folder.</param>
    /// <param name="url">The repository URL.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The URL is unusable, git is not installed, or git refused.</exception>
    Task SetRemote(string folder, string url, CancellationToken cancellation);

    /// <summary>
    /// Sets the git user name and email in the server user's global configuration.
    /// </summary>
    /// <param name="folder">The workspace folder, only where git runs.</param>
    /// <param name="name">The user name.</param>
    /// <param name="email">The email.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <exception cref="RequestException">The name or email is empty, or git refused.</exception>
    Task SetIdentity(string folder, string name, string email, CancellationToken cancellation);

    /// <summary>
    /// Runs git with the server's non-interactive environment and reports how it ended; it never throws for a failing exit code.
    /// </summary>
    /// <param name="folder">The folder git runs in.</param>
    /// <param name="arguments">The arguments, passed without a shell.</param>
    /// <param name="cancellation">Cancels the run and ends the process.</param>
    /// <param name="timeout">How long it may run, or <see langword="null" /> for the default of two minutes.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="RequestException">Git is not installed, the folder is missing or git ran too long.</exception>
    Task<GitOutput> Run(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation, TimeSpan? timeout = null);

    /// <summary>
    /// Runs git like <see cref="Run(string, IReadOnlyList{string}, CancellationToken, TimeSpan?)" /> with extra environment variables, such as the index file to use.
    /// </summary>
    /// <param name="folder">The folder git runs in.</param>
    /// <param name="arguments">The arguments, passed without a shell.</param>
    /// <param name="environment">The environment variables to set for the process.</param>
    /// <param name="cancellation">Cancels the run and ends the process.</param>
    /// <param name="timeout">How long it may run, or <see langword="null" /> for the default of two minutes.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="RequestException">Git is not installed, the folder is missing or git ran too long.</exception>
    Task<GitOutput> Run(string folder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation, TimeSpan? timeout = null);

    /// <summary>
    /// Clones a repository into a new folder.
    /// </summary>
    /// <param name="parent">The existing folder to create the clone's folder in.</param>
    /// <param name="url">The repository URL.</param>
    /// <param name="name">The new folder's name, or <see langword="null" /> to name it after the repository.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The absolute path of the cloned folder.</returns>
    /// <exception cref="RequestException">The URL is unusable, the parent is missing, the folder exists, or the clone failed.</exception>
    Task<string> Clone(string parent, string url, string? name, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class GitService : IGitService
{
    private readonly TimeSpan timeout = TimeSpan.FromMinutes(2);

    private readonly TimeSpan cloneTimeout = TimeSpan.FromMinutes(30);

    private readonly int maxDiffLines = 5000;

    private readonly Regex hash = new("^[0-9a-fA-F]{4,64}$");

    private readonly Regex hunk = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");

    private bool? installed;

    /// <inheritdoc />
    public async Task<GitStatus> Status(string folder, CancellationToken cancellation)
    {
        if (!await IsInstalled(cancellation) || !await IsRoot(folder, cancellation))
        {
            return new GitStatus { IsRepo = false };
        }

        string? branch = await Optional(folder, ["symbolic-ref", "--short", "-q", "HEAD"], cancellation);
        string? head = await Optional(folder, ["log", "-1", "--format=%h%x1f%s"], cancellation);
        string? tag = head is null ? null : await Optional(folder, ["describe", "--tags", "--exact-match", "HEAD"], cancellation);
        string[] parts = head?.Split('\u001f', 2) ?? [];
        GitOutput status = await Run(folder, ["status", "--porcelain"], cancellation);
        return new GitStatus
        {
            IsRepo = true,
            Branch = branch,
            Tag = tag,
            Commit = parts.Length > 0 ? parts[0] : string.Empty,
            Message = parts.Length > 1 ? parts[1] : string.Empty,
            HasChanges = status.Output.Trim().Length > 0,
            Remote = await Optional(folder, ["remote", "get-url", "origin"], cancellation),
            HasIdentity = (await Run(folder, ["var", "GIT_AUTHOR_IDENT"], cancellation)).ExitCode == 0,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitChange>> Changes(string folder, CancellationToken cancellation)
    {
        if (!await IsInstalled(cancellation) || !await IsRoot(folder, cancellation))
        {
            return [];
        }

        GitOutput result = Check(await Run(folder, ["status", "--porcelain=v1", "-z", "--ignored=matching", "--untracked-files=all"], cancellation));
        List<GitChange> changes = [];
        string[] entries = result.Output.Split('\0');

        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];

            if (entry.Length < 4)
            {
                continue;
            }

            string path = entry[3..];

            if (entry[0] is 'R' or 'C')
            {
                i++;
            }

            changes.Add(new GitChange { Path = path, Status = Classify(entry[0], entry[1]) });
        }

        return [.. changes.OrderBy(change => change.Path, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<GitHistory> History(string folder, int skip, int limit, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);

        if (!await HasCommits(folder, cancellation))
        {
            return new GitHistory { Commits = [] };
        }

        string current = await Optional(folder, ["symbolic-ref", "--short", "-q", "HEAD"], cancellation) ?? string.Empty;
        HashSet<string> remotes = [.. (await Run(folder, ["for-each-ref", "--format=%(refname:short)", "refs/remotes"], cancellation)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        GitOutput result = Check(await Run(folder, ["log", "--date-order", "--branches", "--tags", "--remotes", "HEAD", $"--skip={Math.Max(skip, 0)}", $"-n{limit + 1}", "--format=%H%x1f%an%x1f%aI%x1f%D%x1f%s%x1f%P%x1e"], cancellation));
        List<GitCommit> commits = [];

        foreach (string record in result.Output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = record.Trim('\n', '\r').Split('\u001f');

            if (fields.Length < 5)
            {
                continue;
            }

            commits.Add(new GitCommit
            {
                Hash = fields[0],
                Author = fields[1],
                Date = DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture),
                Refs = ParseRefs(fields[3], current, remotes),
                Subject = fields[4],
                Parents = fields.Length > 5 ? [.. fields[5].Split(' ', StringSplitOptions.RemoveEmptyEntries)] : [],
            });
        }

        return new GitHistory { Commits = [.. commits.Take(limit)], HasMore = commits.Count > limit };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitChange>> CommitChanges(string folder, string commit, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);
        RequireHash(commit);
        string? parent = await Parent(folder, commit, cancellation);
        List<string> arguments = parent is null ? ["diff-tree", "--root", "--no-commit-id", "-r", "--name-status", "-z", "-M", commit] : ["diff", "--name-status", "-z", "-M", parent, commit];
        GitOutput result = Check(await Run(folder, arguments, cancellation));
        List<GitChange> changes = [];
        string[] entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i + 1 < entries.Length; i++)
        {
            char code = entries[i][0];
            string path = entries[i + 1];
            i++;

            if (code is 'R' or 'C')
            {
                path = entries[i + 1];
                i++;
            }

            changes.Add(new GitChange { Path = path, Status = code switch { 'A' or 'C' => GitChangeStatus.Added, 'D' => GitChangeStatus.Deleted, _ => GitChangeStatus.Modified } });
        }

        return [.. changes.OrderBy(change => change.Path, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<GitDiff> Diff(string folder, string path, string? commit, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);
        RequirePath(path);
        string text;

        if (commit is not null)
        {
            RequireHash(commit);
            string? parent = await Parent(folder, commit, cancellation);
            text = Check(await Run(folder, parent is null ? ["diff-tree", "-p", "--root", "--no-commit-id", "--no-color", "--no-ext-diff", "-U1000000", commit, "--", path] : ["diff", "--no-color", "--no-ext-diff", "-U1000000", parent, commit, "--", path], cancellation)).Output;
        }
        else
        {
            text = await WorkingTreeDiff(folder, path, cancellation);
        }

        return Parse(path, text);
    }

    /// <inheritdoc />
    public async Task Commit(string folder, string message, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);
        Check(await Run(folder, ["add", "-A"], cancellation));

        if ((await Run(folder, ["diff", "--cached", "--quiet"], cancellation)).ExitCode == 0 && await HasCommits(folder, cancellation))
        {
            throw new RequestException("There is nothing to commit.");
        }

        Check(await Run(folder, ["commit", "-m", message], cancellation));

        if (await Optional(folder, ["remote", "get-url", "origin"], cancellation) is not null && await Optional(folder, ["symbolic-ref", "--short", "-q", "HEAD"], cancellation) is { } branch)
        {
            await Run(folder, ["push", "origin", branch], cancellation);
        }
    }

    /// <inheritdoc />
    public async Task Reset(string folder, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);

        if (await HasCommits(folder, cancellation))
        {
            Check(await Run(folder, ["reset", "--hard"], cancellation));
        }

        Check(await Run(folder, ["clean", "-fd"], cancellation));
    }

    /// <inheritdoc />
    public async Task CreateBranch(string folder, string name, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);

        if (name.StartsWith('-') || (await Run(folder, ["check-ref-format", "--branch", name], cancellation)).ExitCode != 0)
        {
            throw new RequestException($"'{name}' is not a valid branch name.");
        }

        if ((await Run(folder, ["show-ref", "--verify", "--quiet", $"refs/heads/{name}"], cancellation)).ExitCode == 0)
        {
            throw new RequestException($"A branch named '{name}' already exists.");
        }

        Check(await Run(folder, ["checkout", "-b", name], cancellation));

        if (await Optional(folder, ["remote", "get-url", "origin"], cancellation) is not null)
        {
            await Run(folder, ["push", "origin", name, "--set-upstream"], cancellation);
        }
    }

    /// <inheritdoc />
    public async Task CreateTag(string folder, string name, CancellationToken cancellation)
    {
        await RequireRepo(folder, cancellation);

        if (name.StartsWith('-') || (await Run(folder, ["check-ref-format", $"refs/tags/{name}"], cancellation)).ExitCode != 0)
        {
            throw new RequestException($"'{name}' is not a valid tag name.");
        }

        if ((await Run(folder, ["show-ref", "--verify", "--quiet", $"refs/tags/{name}"], cancellation)).ExitCode == 0)
        {
            throw new RequestException($"A tag named '{name}' already exists.");
        }

        if (!await HasCommits(folder, cancellation))
        {
            throw new RequestException("Commit something before tagging.");
        }

        Check(await Run(folder, ["tag", "-a", name, "-m", name], cancellation));

        if (await Optional(folder, ["remote", "get-url", "origin"], cancellation) is not null)
        {
            await Run(folder, ["push", "origin", $"refs/tags/{name}"], cancellation);
        }
    }

    /// <inheritdoc />
    public async Task SetRemote(string folder, string url, CancellationToken cancellation)
    {
        RequireUrl(url);
        await RequireInstalled(cancellation);

        if (!await IsRoot(folder, cancellation))
        {
            if ((await Run(folder, ["init", "-b", "main"], cancellation)).ExitCode != 0)
            {
                Check(await Run(folder, ["init"], cancellation));
            }
        }

        string verb = await Optional(folder, ["remote", "get-url", "origin"], cancellation) is null ? "add" : "set-url";
        Check(await Run(folder, ["remote", verb, "origin", url], cancellation));
    }

    /// <inheritdoc />
    public async Task SetIdentity(string folder, string name, string email, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || name.Any(char.IsControl) || email.Any(char.IsControl))
        {
            throw new RequestException("Enter a name and an email.");
        }

        await RequireInstalled(cancellation);
        Check(await Run(folder, ["config", "--global", "user.name", name.Trim()], cancellation));
        Check(await Run(folder, ["config", "--global", "user.email", email.Trim()], cancellation));
    }

    /// <inheritdoc />
    public async Task<string> Clone(string parent, string url, string? name, CancellationToken cancellation)
    {
        RequireUrl(url);
        await RequireInstalled(cancellation);

        if (!Directory.Exists(parent))
        {
            throw new RequestException($"The folder '{parent}' does not exist.");
        }

        string folderName = string.IsNullOrWhiteSpace(name) ? NameOf(url) : name.Trim();

        if (folderName.Length == 0 || folderName is "." or ".." || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new RequestException($"'{folderName}' is not a usable folder name.");
        }

        string target = Path.Combine(parent, folderName);

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new RequestException($"'{folderName}' already exists in '{parent}'.");
        }

        GitOutput result = await Run(parent, ["clone", "--", url, folderName], cancellation, cloneTimeout);

        if (result.ExitCode != 0)
        {
            TryDelete(target);
            throw new RequestException(Message(result));
        }

        return target;
    }

    private async Task<string> WorkingTreeDiff(string folder, string path, CancellationToken cancellation)
    {
        if (await HasCommits(folder, cancellation))
        {
            string tracked = Check(await Run(folder, ["diff", "--no-color", "--no-ext-diff", "-U1000000", "HEAD", "--", path], cancellation)).Output;

            if (tracked.Length > 0)
            {
                return tracked;
            }
        }

        string full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(full) || (await Run(folder, ["ls-files", "--error-unmatch", "--", path], cancellation)).ExitCode == 0)
        {
            return string.Empty;
        }

        GitOutput added = await Run(folder, ["diff", "--no-index", "--no-color", "--no-ext-diff", "-U1000000", "--", "/dev/null", path], cancellation);

        if (added.ExitCode > 1)
        {
            throw new RequestException(Message(added));
        }

        return added.Output;
    }

    private GitDiff Parse(string path, string text)
    {
        if (text.Contains("\nBinary files ", StringComparison.Ordinal) || text.StartsWith("Binary files ", StringComparison.Ordinal) || text.Contains("\nGIT binary patch", StringComparison.Ordinal))
        {
            return new GitDiff { Path = path, Lines = [], IsBinary = true };
        }

        List<GitDiffLine> lines = [];
        bool inside = false;
        bool truncated = false;
        int oldNumber = 0;
        int newNumber = 0;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (hunk.Match(line) is { Success: true } header)
            {
                inside = true;
                oldNumber = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
                newNumber = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
                continue;
            }

            if (!inside || line.Length == 0)
            {
                continue;
            }

            if (lines.Count >= maxDiffLines)
            {
                truncated = true;
                break;
            }

            switch (line[0])
            {
                case '+':
                    newNumber++;
                    lines.Add(new GitDiffLine { Kind = GitDiffKind.Added, Text = line[1..], NewNumber = newNumber });
                    break;
                case '-':
                    oldNumber++;
                    lines.Add(new GitDiffLine { Kind = GitDiffKind.Removed, Text = line[1..], OldNumber = oldNumber });
                    break;
                case ' ':
                    oldNumber++;
                    newNumber++;
                    lines.Add(new GitDiffLine { Kind = GitDiffKind.Unchanged, Text = line[1..], OldNumber = oldNumber, NewNumber = newNumber });
                    break;
            }
        }

        return new GitDiff { Path = path, Lines = lines, Truncated = truncated };
    }

    private GitChangeStatus Classify(char index, char tree) => (index, tree) switch
    {
        ('?', '?') => GitChangeStatus.Added,
        ('!', '!') => GitChangeStatus.Ignored,
        ('D', _) or (_, 'D') => GitChangeStatus.Deleted,
        ('A', _) => GitChangeStatus.Added,
        _ => GitChangeStatus.Modified,
    };

    private List<GitRef> ParseRefs(string decoration, string current, HashSet<string> remotes)
    {
        List<GitRef> refs = [];

        foreach (string part in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "HEAD")
            {
                continue;
            }

            if (part.StartsWith("tag: ", StringComparison.Ordinal))
            {
                refs.Add(new GitRef { Name = part[5..], Kind = GitRefKind.Tag });
                continue;
            }

            string name = part.StartsWith("HEAD -> ", StringComparison.Ordinal) ? part[8..] : part;

            if (remotes.Contains(name))
            {
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    refs.Add(new GitRef { Name = name, Kind = GitRefKind.Remote });
                }
            }
            else
            {
                refs.Add(new GitRef { Name = name, Kind = GitRefKind.Branch, IsCurrent = name == current });
            }
        }

        return refs;
    }

    private string NameOf(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');
        string last = trimmed[(trimmed.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    private void TryDelete(string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RequireUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-') || url.Any(char.IsControl))
        {
            throw new RequestException("Enter a repository URL.");
        }
    }

    private void RequireHash(string commit)
    {
        if (!hash.IsMatch(commit))
        {
            throw new RequestException("That is not a commit.");
        }
    }

    private void RequirePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains('\0') || path.Split('/', '\\').Contains(".."))
        {
            throw new RequestException("That path is outside the workspace folder.");
        }
    }

    private async Task RequireInstalled(CancellationToken cancellation)
    {
        if (!await IsInstalled(cancellation))
        {
            throw new RequestException("Git is not installed on the server.");
        }
    }

    private async Task RequireRepo(string folder, CancellationToken cancellation)
    {
        await RequireInstalled(cancellation);

        if (!await IsRoot(folder, cancellation))
        {
            throw new RequestException("This workspace is not a git repository.");
        }
    }

    private async Task<bool> IsInstalled(CancellationToken cancellation)
    {
        if (installed is null)
        {
            try
            {
                installed = (await Run(Path.GetTempPath(), ["--version"], cancellation)).ExitCode == 0;
            }
            catch (Win32Exception)
            {
                installed = false;
            }
        }

        return installed.Value;
    }

    private async Task<bool> IsRoot(string folder, CancellationToken cancellation)
    {
        GitOutput result = await Run(folder, ["rev-parse", "--is-inside-work-tree", "--show-prefix"], cancellation);
        string[] lines = result.Output.Split('\n');
        return result.ExitCode == 0 && lines[0].Trim() == "true" && lines.Skip(1).All(line => line.Trim().Length == 0);
    }

    private async Task<bool> HasCommits(string folder, CancellationToken cancellation) => (await Run(folder, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellation)).ExitCode == 0;

    private async Task<string?> Parent(string folder, string commit, CancellationToken cancellation)
    {
        GitOutput result = Check(await Run(folder, ["rev-list", "--parents", "-n", "1", commit], cancellation));
        string[] parts = result.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : null;
    }

    private async Task<string?> Optional(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        GitOutput result = await Run(folder, arguments, cancellation);
        return result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } text ? text : null;
    }

    private GitOutput Check(GitOutput result) => result.ExitCode == 0 ? result : throw new RequestException(Message(result));

    private string Message(GitOutput result) => (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim() is { Length: > 0 } text ? text : "Git failed.";

    /// <inheritdoc />
    public Task<GitOutput> Run(string folder, IReadOnlyList<string> arguments, CancellationToken cancellation, TimeSpan? limit = null) => Run(folder, arguments, new Dictionary<string, string>(), cancellation, limit);

    /// <inheritdoc />
    public async Task<GitOutput> Run(string folder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation, TimeSpan? limit = null)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "file:https:http:git:ssh";

        if (Environment.GetEnvironmentVariable("GIT_SSH_COMMAND") is null)
        {
            start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        using Process process = new() { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Win32Exception) when (!Directory.Exists(folder))
        {
            throw new RequestException($"The folder '{folder}' does not exist.");
        }

        process.StandardInput.Close();
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer.CancelAfter(limit ?? timeout);
        Task<string> output = process.StandardOutput.ReadToEndAsync(timer.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timer.Token);

        try
        {
            await process.WaitForExitAsync(timer.Token);
            return new GitOutput(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            cancellation.ThrowIfCancellationRequested();
            throw new RequestException("Git took too long.");
        }
    }
}
