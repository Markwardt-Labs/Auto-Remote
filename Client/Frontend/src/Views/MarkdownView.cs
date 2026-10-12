namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Renders markdown text, including tables, task lists and code blocks, as native Avalonia controls, with the matches of a search marked.
/// </summary>
internal sealed class MarkdownView : ContentControl
{
    /// <summary>
    /// Identifies the <see cref="Source" /> property.
    /// </summary>
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Source));

    /// <summary>
    /// Identifies the <see cref="Matches" /> property.
    /// </summary>
    public static readonly StyledProperty<TextMatches?> MatchesProperty = AvaloniaProperty.Register<MarkdownView, TextMatches?>(nameof(Matches));

    /// <summary>
    /// Identifies the <see cref="PlainText" /> property.
    /// </summary>
    public static readonly StyledProperty<string> PlainTextProperty = AvaloniaProperty.Register<MarkdownView, string>(nameof(PlainText), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private static readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    static MarkdownView()
    {
        SourceProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        MatchesProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
    }

    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly StringBuilder plain = new();

    private SelectableTextBlock? block;

    private int blockStart;

    private int matchIndex;

    private (SelectableTextBlock Block, int Offset)? current;

    /// <summary>
    /// Gets or sets the markdown text to render.
    /// </summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>
    /// Gets or sets the search matches to mark, as positions in <see cref="PlainText" />.
    /// </summary>
    public TextMatches? Matches
    {
        get => GetValue(MatchesProperty);
        set => SetValue(MatchesProperty, value);
    }

    /// <summary>
    /// Gets the text as laid out on screen, the text blocks in order separated by line breaks, which is what the matches index into.
    /// </summary>
    public string PlainText
    {
        get => GetValue(PlainTextProperty);
        private set => SetValue(PlainTextProperty, value);
    }

    private void Render()
    {
        plain.Clear();
        matchIndex = 0;
        current = null;
        block = null;
        Content = Source is null ? null : Blocks(Markdig.Markdown.Parse(Source, pipeline), 0);
        PlainText = plain.ToString();

        if (Matches is { Navigate: true } && current is var (target, offset))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (target.TextLayout is { } layout)
                {
                    Rect rect = layout.HitTestTextPosition(offset);
                    target.BringIntoView(new Rect(rect.X, rect.Y, Math.Max(rect.Width, 1), rect.Height).Inflate(new Thickness(40, 20)));
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Begin(SelectableTextBlock target)
    {
        block = target;
        blockStart = plain.Length;
    }

    private void End() => plain.Append('\n');

    private void Emit(InlineCollection target, string text, Action<Run>? style = null)
    {
        int start = plain.Length;
        plain.Append(text);
        TextMatches matches = Matches ?? new TextMatches { Starts = [], Length = 0, Current = -1 };
        int at = 0;

        while (at < text.Length)
        {
            int position = start + at;

            while (matchIndex < matches.Starts.Count && matches.Starts[matchIndex] + matches.Length <= position)
            {
                matchIndex++;
            }

            bool hit = matchIndex < matches.Starts.Count && matches.Starts[matchIndex] <= position;
            int end = matchIndex >= matches.Starts.Count ? text.Length : Math.Min(text.Length, (hit ? matches.Starts[matchIndex] + matches.Length : matches.Starts[matchIndex]) - start);
            Run run = new(text[at..end]);
            style?.Invoke(run);

            if (hit)
            {
                bool isCurrent = matchIndex == matches.Current;
                run.Background = Brush(isCurrent ? "FindMatchCurrent" : "FindMatch", Brushes.Orange);

                if (isCurrent && current is null && block is not null)
                {
                    current = (block, position - blockStart);
                }
            }

            target.Add(run);
            at = end;
        }
    }

    private IBrush Brush(string key, IBrush fallback) => Application.Current?.Resources.TryGetResource(key, ActualThemeVariant, out object? value) == true && value is IBrush brush ? brush : fallback;

    private StackPanel Blocks(IEnumerable<Block> blocks, double indent)
    {
        StackPanel panel = new() { Spacing = 10 };

        foreach (Block block in blocks)
        {
            Control? control = Build(block, indent);

            if (control is not null)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private Control? Build(Block block, double indent) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Text(paragraph.Inline, 14, FontWeight.Normal),
        FencedCodeBlock or CodeBlock => Code(block),
        QuoteBlock quote => Quote(quote, indent),
        ListBlock list => List(list, indent),
        ThematicBreakBlock => new Border { Height = 1, Background = Brush("Border", Brushes.Gray), Margin = new Thickness(0, 4) },
        Table table => Tabular(table),
        HtmlBlock html => Plain(html.Lines.ToString(), 13),
        ContainerBlock container => Blocks(container, indent),
        _ => null,
    };

    private SelectableTextBlock Plain(string text, double size)
    {
        SelectableTextBlock result = new() { FontSize = size, TextWrapping = TextWrapping.Wrap };
        Begin(result);
        Emit(result.Inlines!, text);
        End();
        return result;
    }

    private SelectableTextBlock Heading(HeadingBlock heading)
    {
        double[] sizes = [26, 22, 19, 17, 15, 14];
        return Text(heading.Inline, sizes[Math.Clamp(heading.Level, 1, 6) - 1], FontWeight.SemiBold);
    }

    private SelectableTextBlock Text(ContainerInline? inline, double size, FontWeight weight)
    {
        SelectableTextBlock result = new() { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        Begin(result);

        if (inline is not null)
        {
            Fill(result.Inlines!, inline);
        }

        End();
        return result;
    }

    private Border Code(Block source)
    {
        string text = string.Join('\n', ((LeafBlock)source).Lines.Lines.Take(((LeafBlock)source).Lines.Count).Select(line => line.ToString()));
        SelectableTextBlock result = new() { FontFamily = mono, FontSize = 12 };
        Begin(result);
        Emit(result.Inlines!, text);
        End();

        return new Border
        {
            Background = Brush("Elevated", Brushes.DimGray),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = result,
            },
        };
    }

    private Border Quote(QuoteBlock quote, double indent) => new()
    {
        BorderBrush = Brush("Accent", Brushes.Orange),
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 0, 0, 0),
        Opacity = 0.85,
        Child = Blocks(quote, indent),
    };

    private StackPanel List(ListBlock list, double indent)
    {
        StackPanel panel = new() { Spacing = 4 };
        int number = int.TryParse(list.OrderedStart, out int start) ? start : 1;

        foreach (ListItemBlock item in list.OfType<ListItemBlock>())
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            TextBlock marker = new() { Text = list.IsOrdered ? $"{number++}." : "•", MinWidth = 20, Margin = new Thickness(0, 0, 8, 0), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right };
            StackPanel content = Blocks(item, indent + 1);
            content.Spacing = 4;
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private ScrollViewer Tabular(Table table)
    {
        Grid grid = new();
        int columns = table.ColumnDefinitions.Count;

        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        int rowIndex = 0;

        foreach (TableRow row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            int columnIndex = 0;

            foreach (TableCell cell in row.OfType<TableCell>())
            {
                SelectableTextBlock text = new() { FontSize = 13, FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
                Begin(text);

                if (cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline is { } inline)
                {
                    Fill(text.Inlines!, inline);
                }

                End();

                Border border = new()
                {
                    BorderBrush = Brush("Border", Brushes.Gray),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 6),
                    Background = row.IsHeader ? Brush("Elevated", Brushes.DimGray) : null,
                    Child = text,
                };

                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, Math.Min(columnIndex, columns - 1));
                grid.Children.Add(border);
                columnIndex++;
            }

            rowIndex++;
        }

        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = grid };
    }

    private void Fill(InlineCollection target, ContainerInline container)
    {
        for (Markdig.Syntax.Inlines.Inline? node = container.FirstChild; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline literal:
                    Emit(target, literal.Content.ToString());
                    break;
                case CodeInline code:
                    Emit(target, code.Content, run =>
                    {
                        run.FontFamily = mono;
                        run.FontSize = 13;
                        run.Background = Brush("Elevated", Brushes.DimGray);
                    });
                    break;
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        plain.Append('\n');
                        target.Add(new LineBreak());
                    }
                    else
                    {
                        Emit(target, " ");
                    }
                    break;
                case TaskList task:
                    Emit(target, task.Checked ? "☑ " : "☐ ");
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough } : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    Fill(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline link when link.IsImage:
                    Span image = new Italic();
                    Emit(image.Inlines, "[image: ");
                    Fill(image.Inlines, link);
                    Emit(image.Inlines, "]");
                    target.Add(image);
                    break;
                case LinkInline link:
                    Span anchor = new() { Foreground = Brush("Accent", Brushes.Orange), TextDecorations = TextDecorations.Underline };
                    Fill(anchor.Inlines, link);
                    target.Add(anchor);
                    break;
                case AutolinkInline autolink:
                    Emit(target, autolink.Url, run =>
                    {
                        run.Foreground = Brush("Accent", Brushes.Orange);
                        run.TextDecorations = TextDecorations.Underline;
                    });
                    break;
                case HtmlInline html:
                    Emit(target, html.Tag);
                    break;
                case ContainerInline nested:
                    Fill(target, nested);
                    break;
            }
        }
    }
}
