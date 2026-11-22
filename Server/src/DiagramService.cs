namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Draws the diagrams that markdown files describe in Mermaid, so every client shows the same picture without carrying a diagram engine of its own.
/// </summary>
internal interface IDiagramService
{
    /// <summary>
    /// Draws a diagram.
    /// </summary>
    /// <param name="source">The Mermaid text of the diagram.</param>
    /// <param name="cancellation">Stops the wait for the drawing.</param>
    /// <returns>The diagram as SVG, drawn in the dark colors of the clients with every color spelled out, since the SVG renderers of the clients do not evaluate the color functions Mermaid's themes lean on.</returns>
    /// <exception cref="RequestException">The text is empty or too long, or is not a diagram the renderer can draw.</exception>
    Task<string> Render(string source, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class DiagramService : IDiagramService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiagramService" /> class.
    /// </summary>
    public DiagramService()
    {
        svgColors = new SvgColors(palette);
        colors = new Mermaider.Models.RenderOptions
        {
            Bg = palette["--bg"],
            Fg = palette["--fg"],
            Line = palette["--line"],
            Accent = palette["--accent"],
            Muted = palette["--muted"],
            Surface = palette["--surface"],
            Border = palette["--border"],
            LayoutProvider = new DiagramLayout(Mermaider.MermaidRenderer.LayoutProvider),
        };
    }

    private readonly ConcurrentDictionary<string, string> drawn = new();

    private readonly int maxLength = 100_000;

    private readonly int maxDrawings = 128;

    private readonly TimeSpan limit = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyDictionary<string, string> palette = new Dictionary<string, string>
    {
        ["--bg"] = "#2D2D2D",
        ["--fg"] = "#CCCCCC",
        ["--line"] = "#969696",
        ["--accent"] = "#007ACC",
        ["--muted"] = "#969696",
        ["--surface"] = "#252525",
        ["--border"] = "#3C3C3C",
    };

    private readonly Mermaider.Models.RenderOptions colors;

    private readonly ISvgColors svgColors;

    /// <inheritdoc />
    public async Task<string> Render(string source, CancellationToken cancellation)
    {
        string text = source.Trim();

        if (text.Length == 0)
        {
            throw new RequestException("The diagram is empty.");
        }

        if (text.Length > maxLength)
        {
            throw new RequestException("The diagram is too long to draw.");
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        if (drawn.TryGetValue(key, out string? known))
        {
            return known;
        }

        string svg;

        try
        {
            svg = await Task.Run(() => svgColors.Resolve(Mermaider.MermaidRenderer.RenderSvg(text, colors)), CancellationToken.None).WaitAsync(limit, cancellation);
        }
        catch (TimeoutException)
        {
            throw new RequestException("Drawing the diagram took too long.");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or RequestException))
        {
            throw new RequestException($"The diagram cannot be drawn: {exception.Message}");
        }

        if (drawn.Count >= maxDrawings)
        {
            drawn.Clear();
        }

        drawn[key] = svg;
        return svg;
    }
}
