namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SyntaxHighlighterTests
{
    private readonly Mock<ISyntaxLanguages> languages = new();

    private readonly SyntaxLanguages catalog = new();

    private SyntaxHighlighter Using(string path, string text)
    {
        languages.Setup(x => x.Detect(path, text)).Returns(catalog.Detect(path, text));
        languages.Setup(x => x.Find(It.IsAny<string>())).Returns((string name) => catalog.Find(name));
        return new SyntaxHighlighter(languages.Object);
    }

    private string[] Highlight(string path, string text) => Using(path, text).Highlight(path, text).Select(token => $"{token.Kind}:{text.Substring(token.Start, token.Length)}").ToArray();

    [Fact]
    public void Highlight_ReturnsNothingForAnUnknownLanguage()
    {
        languages.Setup(x => x.Detect("a.txt", "if")).Returns((SyntaxLanguage?)null);

        Assert.Empty(new SyntaxHighlighter(languages.Object).Highlight("a.txt", "if"));
    }

    [Fact]
    public void Highlight_ReturnsNothingForTextTooLarge()
    {
        string text = new('a', 100_001);
        languages.Setup(x => x.Detect("a.cs", text)).Returns(catalog.Find("cs"));

        Assert.Empty(new SyntaxHighlighter(languages.Object).Highlight("a.cs", text));
    }

    [Fact]
    public void CSharp_ColorsKeywordsTypesFunctionsStringsNumbersAndComments()
    {
        string[] tokens = Highlight("a.cs", "public class Foo // note\n{ int x = 0x1F; string s = \"a\\\"b\"; Run(); /* c */ }");

        Assert.Equal(["Keyword:public", "Keyword:class", "Type:Foo", "Comment:// note", "Keyword:int", "Number:0x1F", "Keyword:string", "String:\"a\\\"b\"", "Function:Run", "Comment:/* c */"], tokens);
    }

    [Fact]
    public void CSharp_TreatsRawStringsAsOneString() => Assert.Equal(["Keyword:var", "String:\"\"\"a\n\"b\"\"\""], Highlight("a.cs", "var s = \"\"\"a\n\"b\"\"\";"));

    [Fact]
    public void Json_ColorsKeysApartFromValues() => Assert.Equal(["Property:\"name\"", "String:\"x\"", "Property:\"n\"", "Number:12", "Property:\"ok\"", "Keyword:true"], Highlight("a.json", "{\"name\": \"x\", \"n\": 12, \"ok\" : true}"));

    [Fact]
    public void Yaml_ColorsKeysCommentsAndListKeys() => Assert.Equal(["Property:name", "String:'x'", "Comment:# c", "Property:key", "Keyword:true"], Highlight("a.yml", "name: 'x' # c\n  - key: true\n"));

    [Fact]
    public void Yaml_IgnoresColonsInsideValues() => Assert.Equal(["Property:url"], Highlight("a.yml", "url: http://x.y/z"));

    [Fact]
    public void Ini_ColorsSectionsKeysAndComments() => Assert.Equal(["Tag:[core]", "Property:name", "Number:5", "Comment:; note"], Highlight("a.ini", "[core]\nname = 5\n; note"));

    [Fact]
    public void Env_ColorsKeysAndVariables() => Assert.Equal(["Property:PATH", "Variable:${HOME}", "Comment:# x"], Highlight(".env", "export PATH=${HOME}/bin\n# x"));

    [Fact]
    public void Shell_ColorsVariablesAndKeywords() => Assert.Equal(["Comment:#!/bin/sh", "Keyword:if", "Variable:$x", "Keyword:then", "Keyword:echo", "Variable:$(pwd)", "Keyword:fi"], Highlight("a.sh", "#!/bin/sh\nif $x; then echo $(pwd); fi"));

    [Fact]
    public void Sql_MatchesKeywordsRegardlessOfCase() => Assert.Equal(["Keyword:SELECT", "Function:lower", "Keyword:from", "String:'a'", "Comment:-- x"], Highlight("a.sql", "SELECT lower(*) from 'a' -- x"));

    [Fact]
    public void Python_ColorsDecoratorsTripleQuotesAndFunctions() => Assert.Equal(["Type:@cached", "Keyword:def", "Function:run", "String:\"\"\"doc\n\"\"\"", "Keyword:None", "Comment:# c"], Highlight("a.py", "@cached\ndef run():\n    \"\"\"doc\n\"\"\" None # c"));

    [Fact]
    public void Rust_ColorsLifetimesApartFromChars() => Assert.Equal(["Keyword:fn", "Type:'a", "String:'x'"], Highlight("a.rs", "fn f<'a>() 'x'"));

    [Fact]
    public void Css_ColorsPropertiesColorsAndAtRules() => Assert.Equal(["Keyword:@media", "Property:color", "Number:#fff", "Property:margin", "Number:10px", "Comment:/* c */"], Highlight("a.css", "@media x { a { color: #fff; margin: 10px; } /* c */ }"));

    [Fact]
    public void Markup_ColorsTagsAttributesValuesAndComments() => Assert.Equal(["Tag:Project", "Property:Sdk", "String:\"X\"", "Comment:<!-- c -->", "Tag:Item", "Property:Include", "String:\"a\"", "Tag:Project"], Highlight("a.csproj", "<Project Sdk=\"X\"><!-- c --><Item Include=\"a\"/></Project>"));

    [Fact]
    public void Html_HighlightsScriptAndStyleContent() => Assert.Equal(["Tag:script", "Keyword:const", "Tag:script", "Tag:style", "Property:color", "Tag:style"], Highlight("a.html", "<script>const a = 1;</script><style>b { color: red; }</style>").Where(token => !token.StartsWith("Number") && !token.StartsWith("Function")).ToArray());

    [Fact]
    public void Csv_ColorsTheHeaderAndNumbers() => Assert.Equal(["Property:id", "Property:name", "Number:1", "String:\"a,b\"", "Number: 2.5"], Highlight("a.csv", "id,name\n1,\"a,b\", 2.5"));

    [Fact]
    public void Tsv_SplitsOnTabs() => Assert.Equal(["Property:a", "Property:b c", "Number:1"], Highlight("a.tsv", "a\tb c\n1\tx"));

    [Fact]
    public void Ignore_ColorsPatternsAndComments() => Assert.Equal(["Comment:# c", "Property:bin/", "Property:!keep.txt"], Highlight(".gitignore", "# c\nbin/\n!keep.txt"));

    [Fact]
    public void Dockerfile_ColorsInstructionsRegardlessOfCase() => Assert.Equal(["Keyword:FROM", "Keyword:run", "Variable:$X"], Highlight("Dockerfile", "FROM a\nrun $X"));

    [Fact]
    public void Tokens_NeverOverlapAndStayInsideTheText()
    {
        foreach (string path in new[] { "a.cs", "a.json", "a.yml", "a.ini", "a.html", "a.csv", "a.sh", "a.css", "a.ps1", ".env" })
        {
            string text = "<a b=\"c\n/* [x] 'y' \"q $z ${ ( { #fff 0x \\ \"\"\" ''' <!-- <![CDATA[ <script> ";
            int end = 0;

            foreach (SyntaxToken token in Using(path, text).Highlight(path, text))
            {
                Assert.True(token.Start >= end);
                Assert.True(token.Length > 0);
                end = token.Start + token.Length;
                Assert.True(end <= text.Length);
            }
        }
    }
}
