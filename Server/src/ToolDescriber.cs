namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Describes what the AI did with a tool in a few words, so the chat shows the work as a compact line ("Read src/Program.cs", "Run dotnet build") and never carries what the tool was given or gave back, such as the content of a file.
/// </summary>
internal interface IToolDescriber
{
    /// <summary>
    /// Describes a call of a tool.
    /// </summary>
    /// <param name="server">The MCP server that offers the tool, or <see langword="null" /> when it is not known.</param>
    /// <param name="name">The name of the tool.</param>
    /// <param name="input">The arguments of the call, as JSON.</param>
    /// <returns>One short line.</returns>
    string Call(string? server, string name, string input);

    /// <summary>
    /// Describes what a tool gave back, which is a count or a short line and never more.
    /// </summary>
    /// <param name="name">The name of the tool the result belongs to, or <see langword="null" /> when it is not known.</param>
    /// <param name="output">What the tool gave back.</param>
    /// <param name="isError">Whether the call failed.</param>
    /// <returns>One short line.</returns>
    string Result(string? name, string output, bool isError);
}

/// <inheritdoc />
internal sealed class ToolDescriber : IToolDescriber
{
    private readonly int shortest = 120;

    private readonly int longest = 160;

    private readonly string ownServer = "liveai";

    /// <inheritdoc />
    public string Call(string? server, string name, string input)
    {
        JsonElement? arguments = Parse(input);
        string Text(string key) => Argument(arguments, key);

        switch (name.ToLowerInvariant())
        {
            case "read_file":
                return $"Read {Path(Text("path"))}";
            case "write_file":
                return arguments is { } written && written.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String ? $"Write {Path(Text("path"))} ({Count(content.GetString() ?? string.Empty, "line")})" : $"Write {Path(Text("path"))}";
            case "edit_file":
                return $"Edit {Path(Text("path"))}";
            case "list_directory":
                return $"List {Path(Text("path"), ".")}";
            case "find_files":
                return $"Find files matching {Clip(Text("pattern"), 60)}{In(Text("path"))}";
            case "search_files":
                return $"Search for \"{Clip(Text("pattern"), 60)}\"{In(Text("path"))}";
            case "shell":
                return $"Run {Clip(First(Text("command")), shortest)}{(string.Equals(Text("background"), "true", StringComparison.OrdinalIgnoreCase) ? " (in the background)" : string.Empty)}";
            case "copy_file":
                return $"Copy {Path(Text("from"))} to {Path(Text("to"))}";
            case "move_file":
                return $"Move {Path(Text("from"))} to {Path(Text("to"))}";
            case "delete_file":
                return $"Delete {Path(Text("path"))}";
            case "diff_files":
                return $"Compare {Path(Text("path_a"))} with {Path(Text("path_b"))}";
            case "download_url":
                return $"Download {Clip(Text("url"), shortest)}";
            case "fetch_url":
                return $"Fetch {Clip(Text("url"), shortest)}";
            case "view_image":
                return $"View {Path(Text("path"))}";
            case "websearch" or "web_search" or "search_web":
                return $"Search the web for \"{Clip(Text("query"), 80)}\"";
            case "delegate":
                return $"Delegate: {Clip(First(Text("task")), shortest)}";
            case "task_wait":
                return arguments is { } waiting && waiting.TryGetProperty("task_ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 ? $"Wait for {string.Join(", ", ids.EnumerateArray().Select(id => id.ToString()))}" : "Wait for the background tasks";
            case "task_output":
                return $"Read the output of {Text("task_id")}";
            case "task_stop":
                return $"Stop {Text("task_id")}";
            case "current_time":
                return "Check the time";
            case "tool_search":
                return $"Search the tools for \"{Clip(Text("query"), 60)}\"";
            case "ask_user":
                return $"Ask the user: {Clip(First(Text("question")), shortest)}";
            case "respond":
                return "Respond";
            default:
                return Generic(server is { Length: > 0 } && server != ownServer ? $"{server}/{name}" : name, arguments);
        }
    }

    /// <inheritdoc />
    public string Result(string? name, string output, bool isError)
    {
        string text = output.Trim();

        if (isError)
        {
            return text.Length == 0 ? "→ Failed" : $"→ Failed: {Clip(First(text), longest)}";
        }

        if (text.Length == 0)
        {
            return "→ No output";
        }

        int lines = Lines(text);

        return name?.ToLowerInvariant() switch
        {
            "read_file" or "fetch_url" => $"→ {Count(text, "line")}",
            "list_directory" => $"→ {lines} {(lines == 1 ? "entry" : "entries")}",
            "find_files" => $"→ {lines} {(lines == 1 ? "file" : "files")}",
            "search_files" => $"→ {lines} {(lines == 1 ? "match" : "matches")}",
            _ => lines == 1 && text.Length <= shortest ? $"→ {text}" : $"→ {Count(text, "line")}",
        };
    }

    private JsonElement? Parse(string input)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Argument(JsonElement? arguments, string key)
        => arguments is { } found && found.TryGetProperty(key, out JsonElement value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString() : string.Empty;

    private string Generic(string name, JsonElement? arguments)
    {
        if (arguments is not { } found)
        {
            return name;
        }

        List<string> pairs = [.. found.EnumerateObject().Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False).Take(3).Select(property => $"{property.Name}={Clip(First(property.Value.ToString()), 40)}")];
        return pairs.Count == 0 ? name : $"{name} ({string.Join(", ", pairs)})";
    }

    private string Path(string path, string fallback = "?") => path.Length == 0 ? fallback : Clip(path, shortest);

    private string In(string path) => path.Length == 0 ? string.Empty : $" in {Clip(path, shortest)}";

    private string First(string text)
    {
        string trimmed = text.Trim();
        int end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : $"{trimmed[..end]} ...";
    }

    private string Clip(string text, int length) => text.Length > length ? $"{text[..length]}..." : text;

    private int Lines(string text) => text.TrimEnd('\r', '\n').Split('\n').Length;

    private string Count(string text, string unit)
    {
        int count = Lines(text);
        return $"{count} {unit}{(count == 1 ? string.Empty : "s")}";
    }
}
