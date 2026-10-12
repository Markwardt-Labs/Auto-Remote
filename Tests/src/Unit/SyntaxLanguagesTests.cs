namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SyntaxLanguagesTests
{
    private readonly SyntaxLanguages languages = new();

    [Theory]
    [InlineData("a/B.cs", "cs")]
    [InlineData("data.json", "json")]
    [InlineData("ci.yaml", "yaml")]
    [InlineData("ci.yml", "yaml")]
    [InlineData("a.xml", "xml")]
    [InlineData("app.js", "js")]
    [InlineData("a.c", "c")]
    [InlineData("a.h", "c")]
    [InlineData("A.java", "java")]
    [InlineData("a.py", "py")]
    [InlineData("a.css", "css")]
    [InlineData("a.ts", "ts")]
    [InlineData("a.php", "php")]
    [InlineData("a.rb", "rb")]
    [InlineData("a.go", "go")]
    [InlineData("a.kt", "kt")]
    [InlineData("a.sql", "sql")]
    [InlineData("a.ini", "ini")]
    [InlineData("a.cpp", "cpp")]
    [InlineData("a.rs", "rs")]
    [InlineData("a.pl", "pl")]
    [InlineData(".gitignore", "ignore")]
    [InlineData("sub\\.gitattributes", "gitattributes")]
    [InlineData("a.csv", "csv")]
    [InlineData("a.tsv", "tsv")]
    [InlineData("a.dockerfile", "dockerfile")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData("Dockerfile.dev", "dockerfile")]
    [InlineData("a.sh", "sh")]
    [InlineData("a.conf", "conf")]
    [InlineData(".env", "env")]
    [InlineData(".env.local", "env")]
    [InlineData("a.ps1", "ps1")]
    [InlineData("a.bash", "sh")]
    [InlineData("page.html", "html")]
    [InlineData("App.csproj", "xml")]
    [InlineData("App.sln", "sln")]
    [InlineData("App.slnx", "xml")]
    [InlineData("Makefile", "make")]
    [InlineData("A.AXAML", "xml")]
    public void Detect_RecognizesTheFileByItsName(string path, string expected) => Assert.Equal(expected, languages.Detect(path, string.Empty)?.Name);

    [Theory]
    [InlineData("tool", "#!/usr/bin/env python3\nprint(1)", "py")]
    [InlineData("tool", "#!/bin/bash\n", "sh")]
    [InlineData("tool", "#!/usr/bin/env node\n", "js")]
    [InlineData("data", "<?xml version=\"1.0\"?><a/>", "xml")]
    [InlineData("page", "<!DOCTYPE html>\n<p>", "html")]
    public void Detect_FallsBackToTheFirstLineWhenTheNameSaysNothing(string path, string text, string expected) => Assert.Equal(expected, languages.Detect(path, text)?.Name);

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("README")]
    [InlineData("image.png")]
    public void Detect_ReturnsNullForUnknownFiles(string path) => Assert.Null(languages.Detect(path, "just words"));

    [Fact]
    public void Detect_PrefersTheNameOverTheFirstLine() => Assert.Equal("json", languages.Detect("a.json", "#!/bin/bash")?.Name);

    [Fact]
    public void Find_ReturnsALanguageByNameOrNull()
    {
        Assert.Equal("js", languages.Find("js")?.Name);
        Assert.Null(languages.Find("nothing"));
    }
}
