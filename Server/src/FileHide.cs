namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Decides which entries of a workspace folder the <c>.filehide</c> files hide from the file system panel.
/// </summary>
internal interface IFileHide
{
    /// <summary>
    /// Removes the entries that a <c>.filehide</c> file hides.
    /// </summary>
    /// <param name="root">The workspace folder.</param>
    /// <param name="folder">The listed folder, relative to the workspace folder with <c>/</c> separators; empty for the workspace folder.</param>
    /// <param name="entries">The entries of the listed folder.</param>
    /// <returns>The entries that are not hidden, in their order.</returns>
    IReadOnlyList<FileEntry> Apply(string root, string folder, IReadOnlyList<FileEntry> entries);
}

/// <inheritdoc />
internal sealed class FileHide : IFileHide
{
    private readonly string fileName = ".filehide";

    private readonly RegexOptions options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant;

    /// <inheritdoc />
    public IReadOnlyList<FileEntry> Apply(string root, string folder, IReadOnlyList<FileEntry> entries)
    {
        string[] segments = folder.Length == 0 ? [] : folder.Split('/');
        List<(string Base, List<Rule> Rules)> sets = [];

        for (int depth = 0; depth <= segments.Length; depth++)
        {
            string relative = string.Join('/', segments.Take(depth));
            List<Rule> rules = Load(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar), fileName));

            if (rules.Count > 0)
            {
                sets.Add((relative, rules));
            }
        }

        if (sets.Count == 0)
        {
            return entries;
        }

        bool hiddenFolder = false;

        for (int depth = 1; depth <= segments.Length && !hiddenFolder; depth++)
        {
            hiddenFolder = Matches(sets, string.Join('/', segments.Take(depth)), true);
        }

        return hiddenFolder ? [] : [.. entries.Where(entry => !Matches(sets, entry.Path, entry.IsFolder))];
    }

    private bool Matches(List<(string Base, List<Rule> Rules)> sets, string path, bool folder)
    {
        bool hidden = false;

        foreach ((string folderBase, List<Rule> rules) in sets)
        {
            string relative;

            if (folderBase.Length == 0)
            {
                relative = path;
            }
            else if (path.StartsWith(folderBase + "/", StringComparison.Ordinal))
            {
                relative = path[(folderBase.Length + 1)..];
            }
            else
            {
                continue;
            }

            foreach (Rule rule in rules)
            {
                if ((folder || !rule.FolderOnly) && rule.Pattern.IsMatch(relative))
                {
                    hidden = !rule.Negated;
                }
            }
        }

        return hidden;
    }

    private List<Rule> Load(string file)
    {
        List<Rule> rules = [];

        try
        {
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (Parse(line) is { } rule)
                    {
                        rules.Add(rule);
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return rules;
    }

    private Rule? Parse(string line)
    {
        string text = line.TrimEnd('\r', '\n');
        int end = text.Length;

        while (end > 0 && text[end - 1] == ' ' && (end < 2 || text[end - 2] != '\\'))
        {
            end--;
        }

        text = text[..end];

        if (text.Length == 0 || text[0] == '#')
        {
            return null;
        }

        bool negated = text[0] == '!';

        if (negated)
        {
            text = text[1..];
        }

        bool folderOnly = text.EndsWith('/');
        text = text.TrimEnd('/');

        if (text.Length == 0)
        {
            return null;
        }

        bool anchored = text.Contains('/');
        text = text.TrimStart('/');

        if (text.Length == 0)
        {
            return null;
        }

        string prefix = anchored ? "^" : "^(?:.*/)?";

        try
        {
            return new Rule(new Regex(prefix + Translate(text) + "$", options | RegexOptions.Singleline), negated, folderOnly);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string Translate(string glob)
    {
        StringBuilder pattern = new();

        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];

            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool atStart = i == 0 || glob[i - 1] == '/';
                bool beforeSlash = i + 2 < glob.Length && glob[i + 2] == '/';

                if (atStart && beforeSlash)
                {
                    pattern.Append("(?:.*/)?");
                    i += 2;
                }
                else if (atStart && i + 2 == glob.Length)
                {
                    pattern.Append(".+");
                    i++;
                }
                else
                {
                    pattern.Append("[^/]*");
                    i++;
                }
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else if (c == '[' && glob.IndexOf(']', i + 2 < glob.Length ? i + 2 : glob.Length) is > 0 and int close)
            {
                string body = glob[(i + 1)..close];
                pattern.Append('[').Append(body.StartsWith('!') ? "^" + body[1..] : body).Append(']');
                i = close;
            }
            else if (c == '\\' && i + 1 < glob.Length)
            {
                pattern.Append(Regex.Escape(glob[++i].ToString()));
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        return pattern.ToString();
    }

    private sealed record Rule(Regex Pattern, bool Negated, bool FolderOnly);
}
