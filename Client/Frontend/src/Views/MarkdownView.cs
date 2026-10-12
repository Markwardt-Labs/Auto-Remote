namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Renders markdown text, including tables, task lists and code blocks, as native Avalonia controls.
/// </summary>
public sealed class MarkdownView : ContentControl
{
    /// <summary>
    /// Identifies the <see cref="Source" /> property.
    /// </summary>
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Source));

    private static readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    static MarkdownView() => SourceProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());

    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>
    /// Gets or sets the markdown text to render.
    /// </summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private void Render() => Content = Source is null ? null : Blocks(Markdig.Markdown.Parse(Source, pipeline), 0);

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

    private SelectableTextBlock Plain(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    private SelectableTextBlock Heading(HeadingBlock heading)
    {
        double[] sizes = [26, 22, 19, 17, 15, 14];
        return Text(heading.Inline, sizes[Math.Clamp(heading.Level, 1, 6) - 1], FontWeight.SemiBold);
    }

    private SelectableTextBlock Text(ContainerInline? inline, double size, FontWeight weight)
    {
        SelectableTextBlock block = new() { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };

        if (inline is not null)
        {
            Fill(block.Inlines!, inline);
        }

        return block;
    }

    private Border Code(Block block)
    {
        string text = string.Join('\n', ((LeafBlock)block).Lines.Lines.Take(((LeafBlock)block).Lines.Count).Select(line => line.ToString()));

        return new Border
        {
            Background = Brush("Elevated", Brushes.DimGray),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new SelectableTextBlock { Text = text, FontFamily = mono, FontSize = 12 },
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

                if (cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline is { } inline)
                {
                    Fill(text.Inlines!, inline);
                }

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
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = mono, FontSize = 13, Background = Brush("Elevated", Brushes.DimGray) });
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough } : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    Fill(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline link when link.IsImage:
                    Span image = new Italic();
                    Fill(image.Inlines, link);
                    image.Inlines.Insert(0, new Run("[image: "));
                    image.Inlines.Add(new Run("]"));
                    target.Add(image);
                    break;
                case LinkInline link:
                    Span anchor = new() { Foreground = Brush("Accent", Brushes.Orange), TextDecorations = TextDecorations.Underline };
                    Fill(anchor.Inlines, link);
                    target.Add(anchor);
                    break;
                case AutolinkInline autolink:
                    target.Add(new Run(autolink.Url) { Foreground = Brush("Accent", Brushes.Orange), TextDecorations = TextDecorations.Underline });
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case ContainerInline nested:
                    Fill(target, nested);
                    break;
            }
        }
    }
}
