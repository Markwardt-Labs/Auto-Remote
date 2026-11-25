namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ToolDescriberTests
{
    private readonly ToolDescriber describer = new();

    [Theory]
    [InlineData("liveai", "read_file", """{"path":"src/Program.cs"}""", "Read src/Program.cs")]
    [InlineData("liveai", "write_file", """{"path":"a.txt","content":"one\ntwo\nthree"}""", "Write a.txt (3 lines)")]
    [InlineData("liveai", "write_file", """{"path":"a.txt","content":"x"}""", "Write a.txt (1 line)")]
    [InlineData("liveai", "edit_file", """{"path":"a.txt","old_string":"a long piece of the old text","new_string":"new"}""", "Edit a.txt")]
    [InlineData("liveai", "list_directory", """{}""", "List .")]
    [InlineData("liveai", "list_directory", """{"path":"src"}""", "List src")]
    [InlineData("liveai", "find_files", """{"pattern":"*.cs","path":"src"}""", "Find files matching *.cs in src")]
    [InlineData("liveai", "search_files", """{"pattern":"TODO"}""", "Search for \"TODO\"")]
    [InlineData("liveai", "shell", """{"command":"dotnet build\ndotnet test"}""", "Run dotnet build ...")]
    [InlineData("liveai", "shell", """{"command":"sleep 100","background":true}""", "Run sleep 100 (in the background)")]
    [InlineData("liveai", "copy_file", """{"from":"a","to":"b"}""", "Copy a to b")]
    [InlineData("liveai", "move_file", """{"from":"a","to":"b"}""", "Move a to b")]
    [InlineData("liveai", "delete_file", """{"path":"a"}""", "Delete a")]
    [InlineData("liveai", "diff_files", """{"path_a":"a","path_b":"b"}""", "Compare a with b")]
    [InlineData("liveai", "fetch_url", """{"url":"https://x.test"}""", "Fetch https://x.test")]
    [InlineData("liveai", "download_url", """{"url":"https://x.test/a.zip","path":"a.zip"}""", "Download https://x.test/a.zip")]
    [InlineData("liveai", "view_image", """{"path":"a.png"}""", "View a.png")]
    [InlineData(null, "WebSearch", """{"query":"mermaid zoom"}""", "Search the web for \"mermaid zoom\"")]
    [InlineData("liveai", "delegate", """{"task":"Review the tests"}""", "Delegate: Review the tests")]
    [InlineData("liveai", "task_wait", """{"task_ids":["task-1","task-2"],"timeout_seconds":60}""", "Wait for task-1, task-2")]
    [InlineData("liveai", "task_output", """{"task_id":"task-1"}""", "Read the output of task-1")]
    [InlineData("liveai", "task_stop", """{"task_id":"task-1"}""", "Stop task-1")]
    [InlineData("liveai", "current_time", """{}""", "Check the time")]
    [InlineData("liveai", "ask_user", """{"question":"Which one?"}""", "Ask the user: Which one?")]
    [InlineData("liveai", "respond", """{"answer":"fine"}""", "Respond")]
    [InlineData("notes", "add_note", """{"title":"Plan","text":"x","done":false,"deep":{"a":1}}""", "notes/add_note (title=Plan, text=x, done=False)")]
    [InlineData("liveai", "unknown_tool", "not json", "unknown_tool")]
    public void Call_DescribesTheWorkInAFewWords(string? server, string name, string input, string expected)
        => Assert.Equal(expected, describer.Call(server, name, input));

    [Fact]
    public void Call_NeverCarriesWhatWasWrittenOrTheTextThatWasReplaced()
    {
        string content = string.Join('\n', Enumerable.Range(0, 500).Select(line => $"line {line} of a big file"));

        string written = describer.Call("liveai", "write_file", $$"""{"path":"big.txt","content":{{JsonSerializer.Serialize(content)}}}""");
        string edited = describer.Call("liveai", "edit_file", $$"""{"path":"big.txt","old_string":{{JsonSerializer.Serialize(content)}},"new_string":"x"}""");

        Assert.Equal("Write big.txt (500 lines)", written);
        Assert.Equal("Edit big.txt", edited);
    }

    [Fact]
    public void Call_ClipsLongPathsAndCommands()
    {
        string line = describer.Call("liveai", "shell", $$"""{"command":"{{new string('x', 400)}}"}""");

        Assert.True(line.Length < 140);
        Assert.EndsWith("...", line);
    }

    [Theory]
    [InlineData("read_file", "line one\nline two\nline three\n", false, "→ 3 lines")]
    [InlineData("fetch_url", "<html>\n<body>\n</html>", false, "→ 3 lines")]
    [InlineData("list_directory", "a/\nb.txt\nc.txt", false, "→ 3 entries")]
    [InlineData("list_directory", "a/", false, "→ 1 entry")]
    [InlineData("find_files", "a.cs\nb.cs", false, "→ 2 files")]
    [InlineData("search_files", "a.cs:1:x\nb.cs:2:x\nc.cs:3:x", false, "→ 3 matches")]
    [InlineData("shell", "built", false, "→ built")]
    [InlineData("shell", "one\ntwo\nthree", false, "→ 3 lines")]
    [InlineData("write_file", "Wrote hello.txt (1 lines).", false, "→ Wrote hello.txt (1 lines).")]
    [InlineData("shell", "  ", false, "→ No output")]
    [InlineData("read_file", "no such file\nsecond line", true, "→ Failed: no such file ...")]
    [InlineData("shell", "", true, "→ Failed")]
    [InlineData(null, "ok", false, "→ ok")]
    public void Result_IsACountOrAShortLineAndNeverTheContent(string? name, string output, bool isError, string expected)
        => Assert.Equal(expected, describer.Result(name, output, isError));

    [Fact]
    public void Result_NeverCarriesTheContentOfAFileOrAHugeOutput()
    {
        string content = string.Join('\n', Enumerable.Range(0, 800).Select(line => $"secret line {line}"));

        Assert.Equal("→ 800 lines", describer.Result("read_file", content, false));
        Assert.Equal("→ 800 lines", describer.Result("shell", content, false));
        Assert.Equal("→ 1 line", describer.Result(null, new string('x', 5000), false));
        Assert.DoesNotContain("secret", describer.Result("read_file", content, false));
    }
}
