namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Shows a file's text with line numbers in a monospaced font, colored by its syntax when its language is recognized, with the matches of a search marked.
/// </summary>
internal sealed class SyntaxView : ContentControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyntaxView" /> class.
    /// </summary>
    public SyntaxView()
    {
        gutter = new TextBlock { FontFamily = mono, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 12, 0), IsHitTestVisible = false };
        block = new SelectableTextBlock { FontFamily = mono, FontSize = 12, Background = Brushes.Transparent };
        scroller = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = block };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        block.LayoutUpdated += (_, _) => NumberLines();
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>
    /// Identifies the <see cref="File" /> property.
    /// </summary>
    public static readonly StyledProperty<FileContent?> FileProperty = AvaloniaProperty.Register<SyntaxView, FileContent?>(nameof(File));

    /// <summary>
    /// Identifies the <see cref="Matches" /> property.
    /// </summary>
    public static readonly StyledProperty<TextMatches?> MatchesProperty = AvaloniaProperty.Register<SyntaxView, TextMatches?>(nameof(Matches));

    /// <summary>
    /// Identifies the <see cref="Wrap" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> WrapProperty = AvaloniaProperty.Register<SyntaxView, bool>(nameof(Wrap));

    static SyntaxView()
    {
        WrapProperty.Changed.AddClassHandler<SyntaxView>((view, _) => view.Rewrap());
        FileProperty.Changed.AddClassHandler<SyntaxView>((view, _) => view.Load());
        MatchesProperty.Changed.AddClassHandler<SyntaxView>((view, _) => view.Render());
    }

    private readonly ISyntaxHighlighter highlighter = new SyntaxHighlighter(new SyntaxLanguages());

    private readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly TextBlock gutter;

    private readonly SelectableTextBlock block;

    private readonly ScrollViewer scroller;

    private TextLayout? numbered;

    private IReadOnlyList<SyntaxToken> tokens = [];

    /// <summary>
    /// Gets or sets the file to show.
    /// </summary>
    public FileContent? File
    {
        get => GetValue(FileProperty);
        set => SetValue(FileProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether long lines wrap instead of scrolling sideways.
    /// </summary>
    public bool Wrap
    {
        get => GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    /// <summary>
    /// Gets or sets the search matches to mark.
    /// </summary>
    public TextMatches? Matches
    {
        get => GetValue(MatchesProperty);
        set => SetValue(MatchesProperty, value);
    }

    private IBrush? Brush(string key) => Application.Current?.Resources.TryGetResource(key, ActualThemeVariant, out object? value) == true ? value as IBrush : null;

    private void Load()
    {
        string text = File?.Text ?? string.Empty;
        tokens = File is { } file ? highlighter.Highlight(file.Path, text) : [];
        gutter.Foreground = Brush("LineNumber");
        NumberLines();
        Render();
    }

    private void Rewrap()
    {
        block.TextWrapping = Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        scroller.HorizontalScrollBarVisibility = Wrap ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        NumberLines();
    }

    private void NumberLines()
    {
        if (File is not { } file)
        {
            gutter.Text = string.Empty;
            return;
        }

        string text = file.Text;

        if (!Wrap || block.TextLayout is not { } layout)
        {
            numbered = null;
            gutter.Text = string.Join('\n', Enumerable.Range(1, text.Count(c => c == '\n') + 1));
            return;
        }

        if (ReferenceEquals(layout, numbered))
        {
            return;
        }

        numbered = layout;

        StringBuilder numbers = new();
        int number = 0;

        foreach (TextLine line in layout.TextLines)
        {
            int start = line.FirstTextSourceIndex;

            if (start == 0 || (start <= text.Length && text[start - 1] == '\n'))
            {
                number++;
                numbers.Append(number);
            }

            numbers.Append('\n');
        }

        gutter.Text = numbers.ToString().TrimEnd('\n');
    }

    private void Render()
    {
        block.Inlines!.Clear();
        block.Text = null;

        if (File is not { } file)
        {
            return;
        }

        TextMatches matches = Matches ?? new TextMatches { Starts = [], Length = 0, Current = -1 };

        if (tokens.Count == 0 && matches.Starts.Count == 0)
        {
            block.Text = file.Text;
            return;
        }

        SortedSet<int> cuts = [0, file.Text.Length];

        foreach (SyntaxToken token in tokens)
        {
            cuts.Add(token.Start);
            cuts.Add(token.Start + token.Length);
        }

        foreach (int start in matches.Starts)
        {
            cuts.Add(start);
            cuts.Add(start + matches.Length);
        }

        int[] points = [.. cuts];
        int tokenAt = 0;
        int matchAt = 0;

        for (int i = 0; i + 1 < points.Length; i++)
        {
            int from = points[i];

            while (tokenAt < tokens.Count && tokens[tokenAt].Start + tokens[tokenAt].Length <= from)
            {
                tokenAt++;
            }

            while (matchAt < matches.Starts.Count && matches.Starts[matchAt] + matches.Length <= from)
            {
                matchAt++;
            }

            Run run = new(file.Text[from..points[i + 1]]);

            if (tokenAt < tokens.Count && tokens[tokenAt].Start <= from)
            {
                run.Foreground = Brush("Syntax" + tokens[tokenAt].Kind);
            }

            if (matchAt < matches.Starts.Count && matches.Starts[matchAt] <= from)
            {
                run.Background = Brush(matchAt == matches.Current ? "FindMatchCurrent" : "FindMatch");
            }

            block.Inlines.Add(run);
        }

        if (matches.Navigate && matches.Current >= 0)
        {
            int position = matches.Starts[matches.Current];
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Rect rect = block.TextLayout.HitTestTextPosition(position);
                block.BringIntoView(new Rect(rect.X, rect.Y, Math.Max(rect.Width, 1), rect.Height).Inflate(new Thickness(40, 20)));
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }
}
