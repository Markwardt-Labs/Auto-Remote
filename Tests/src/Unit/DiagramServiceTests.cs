namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class DiagramServiceTests
{
    private readonly DiagramService service = new();

    [Theory]
    [InlineData("graph TD\n  A[Start] --> B{Decision}\n  B -->|Yes| C[OK]\n  B -->|No| D[End]", "Decision")]
    [InlineData("sequenceDiagram\n  Alice->>Bob: Hello\n  Bob-->>Alice: Hi", "Hello")]
    [InlineData("pie title Pets\n  \"Dogs\" : 5\n  \"Cats\" : 3", "Pets")]
    [InlineData("stateDiagram-v2\n  [*] --> Idle\n  Idle --> Busy", "Busy")]
    public async Task Render_DrawsTheDiagramAsAnSvgThatHoldsItsText(string source, string text)
    {
        string svg = await service.Render(source, CancellationToken.None);

        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains(text, svg);
    }

    [Fact]
    public async Task Render_SpellsEveryColorOutSoRenderersWithoutColorFunctionsDrawItRight()
    {
        string svg = await service.Render("graph LR\n  A --> B", CancellationToken.None);

        Assert.Contains("#2D2D2D", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#CCCCCC", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#252525", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_KeepsWhatItDrewSoTheSameTextIsNotDrawnTwice()
    {
        string first = await service.Render("graph LR\n  A --> B", CancellationToken.None);
        string second = await service.Render("  graph LR\n  A --> B\n\n", CancellationToken.None);

        Assert.Same(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public async Task Render_RefusesEmptyText(string source)
    {
        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Render(source, CancellationToken.None));

        Assert.Equal("The diagram is empty.", exception.Message);
    }

    [Fact]
    public async Task Render_RefusesTextThatIsTooLong()
    {
        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Render("graph TD\n" + string.Join("\n", Enumerable.Range(0, 20000).Select(number => $"  N{number} --> N{number + 1}")), CancellationToken.None));

        Assert.Equal("The diagram is too long to draw.", exception.Message);
    }

    [Fact]
    public async Task Render_ReportsTextThatIsNoDiagramAsARefusalNotACrash()
    {
        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Render("this is not a diagram at all", CancellationToken.None));

        Assert.StartsWith("The diagram cannot be drawn", exception.Message);
    }

    [Fact]
    public async Task Render_DrawsAnEntityRelationshipDiagramThatRelatesAnEntityToItself()
    {
        string svg = await service.Render("erDiagram\n  Folder ||--o{ Folder : \"parent of\"\n  Folder ||--o{ File : holds\n  Folder {\n    int id PK\n  }", CancellationToken.None);

        Assert.Contains("parent of", svg);
        Assert.Contains("holds", svg);
    }

    [Fact]
    public async Task Render_LeavesNoColorFunctionForRenderersThatCannotEvaluateThem()
    {
        string svg = await service.Render("flowchart TD\n  subgraph Group\n    A{Decision} --> B[(Store)]\n  end\n  B --> C([Done])", CancellationToken.None);

        Assert.DoesNotContain("color-mix", svg, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"var\(--(?!_|fs-)", svg);
    }

    [Fact]
    public async Task Render_DrawsALoopBackToTheEntryAsABackEdgeAndTheEntryAboveTheRest()
    {
        string svg = await service.Render("flowchart TD\n  Start([Start]) --> Input[Read]\n  Input --> Valid{Valid}\n  Valid -->|No| Err[Error]\n  Err --> Input\n  Valid -->|Yes| Done([Done])", CancellationToken.None);

        double Top(string id) => double.Parse(System.Text.RegularExpressions.Regex.Match(svg, $@"data-id=""{id}""[\s\S]*?<text x=""[\d.]+"" y=""([\d.]+)""").Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.True(Top("Start") < Top("Input"));
        Assert.True(Top("Input") < Top("Valid"));
        Assert.True(Top("Valid") < Top("Done"));
    }
}
