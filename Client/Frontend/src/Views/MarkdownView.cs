namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Renders markdown text, including tables, task lists and code blocks, as native Avalonia controls, with the matches of a search marked. When collapsible, each heading folds away the content under it up to the next heading of the same or a higher level.
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

    /// <summary>
    /// Identifies the <see cref="Collapsible" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> CollapsibleProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(Collapsible));

    /// <summary>
    /// Identifies the <see cref="AllCollapsed" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> AllCollapsedProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(AllCollapsed), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>
    /// Identifies the <see cref="Numbered" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> NumberedProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(Numbered));

    /// <summary>
    /// Identifies the <see cref="Outline" /> property.
    /// </summary>
    internal static readonly StyledProperty<IReadOnlyList<MarkdownHeading>> OutlineProperty = AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownHeading>>(nameof(Outline), [], defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    /// <summary>
    /// Identifies the <see cref="Trail" /> property.
    /// </summary>
    internal static readonly StyledProperty<IReadOnlyList<MarkdownHeading>> TrailProperty = AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownHeading>>(nameof(Trail), [], defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private static readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    static MarkdownView()
    {
        SourceProperty.Changed.AddClassHandler<MarkdownView>((view, _) =>
        {
            view.collapsed.Clear();
            view.Render();
        });
        CollapsibleProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        AllCollapsedProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.CollapseAll());
        MatchesProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        NumberedProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.ShowNumbers());
    }

    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly StringBuilder plain = new();

    private ScrollSafeTextBlock? block;

    private int blockStart;

    private int matchIndex;

    private (ScrollSafeTextBlock Block, int Offset, int Section)? current;

    private readonly List<Section> sections = [];

    private readonly List<bool> collapsed = [];

    private readonly List<MarkdownHeading> headings = [];

    private readonly FontWeight[] weights = [FontWeight.Bold, FontWeight.SemiBold, FontWeight.SemiBold, FontWeight.Medium, FontWeight.Medium, FontWeight.Normal];

    private readonly double[] fades = [1, 1, 0.95, 0.9, 0.85, 0.8];

    private readonly double barHeight = 30;

    private int containing = -1;

    private bool syncing;

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
    /// Gets or sets a value indicating whether headings fold away the content under them.
    /// </summary>
    public bool Collapsible
    {
        get => GetValue(CollapsibleProperty);
        set => SetValue(CollapsibleProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether every heading is collapsed. Setting it collapses or expands them all, and folding a single heading updates it.
    /// </summary>
    public bool AllCollapsed
    {
        get => GetValue(AllCollapsedProperty);
        set => SetValue(AllCollapsedProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether each heading shows its section number, such as 2.1.3, in front of its text.
    /// </summary>
    public bool Numbered
    {
        get => GetValue(NumberedProperty);
        set => SetValue(NumberedProperty, value);
    }

    /// <summary>
    /// Gets the headings of the document, nested ones included, in reading order; empty unless <see cref="Collapsible" />.
    /// </summary>
    internal IReadOnlyList<MarkdownHeading> Outline
    {
        get => GetValue(OutlineProperty);
        private set => SetValue(OutlineProperty, value);
    }

    /// <summary>
    /// Gets the headings the top of the view is under, from the outermost to the innermost, as <see cref="UpdateTrail" /> last set them; empty at the very top.
    /// </summary>
    internal IReadOnlyList<MarkdownHeading> Trail
    {
        get => GetValue(TrailProperty);
        private set => SetValue(TrailProperty, value);
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
        sections.Clear();
        containing = -1;
        Content = Source is null ? null : Blocks(Markdig.Markdown.Parse(Source, pipeline), 0, Collapsible);
        PlainText = plain.ToString();
        Annotate();

        while (collapsed.Count < sections.Count)
        {
            collapsed.Add(false);
        }

        collapsed.RemoveRange(sections.Count, collapsed.Count - sections.Count);

        if (Matches is { Navigate: true } && current is var (_, _, section))
        {
            for (int at = section; at >= 0; at = sections[at].Parent)
            {
                collapsed[at] = false;
            }
        }

        Fold();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTrail(this.FindAncestorOfType<ScrollViewer>()?.Offset.Y ?? 0), Avalonia.Threading.DispatcherPriority.Loaded);

        if (Matches is { Navigate: true } && current is var (target, offset, _))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (target.TextLayout is { } layout)
                {
                    Rect rect = layout.HitTestTextPosition(offset);
                    target.BringIntoView(new Rect(rect.X, rect.Y, Math.Max(rect.Width, 1), rect.Height).Inflate(new Thickness(40, barHeight + 10, 40, 20)));
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Fold()
    {
        for (int i = 0; i < sections.Count; i++)
        {
            sections[i].Frame.IsVisible = !collapsed[i];
            sections[i].Chevron.Content = collapsed[i] ? "▸" : "▾";
        }

        syncing = true;
        AllCollapsed = sections.Count > 0 && collapsed.All(state => state);
        syncing = false;
    }

    private void CollapseAll()
    {
        if (syncing)
        {
            return;
        }

        for (int i = 0; i < collapsed.Count; i++)
        {
            collapsed[i] = AllCollapsed;
        }

        Fold();
    }

    /// <summary>
    /// Scrolls a heading to just below the breadcrumb bar, opening the sections it is nested under and its own.
    /// </summary>
    /// <param name="index">The <see cref="MarkdownHeading.Index" /> of the heading.</param>
    internal void ScrollTo(int index)
    {
        if (index < 0 || index >= sections.Count)
        {
            return;
        }

        for (int at = index; at >= 0; at = sections[at].Parent)
        {
            collapsed[at] = false;
        }

        Fold();
        UpdateLayout();

        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller && sections[index].Head.TranslatePoint(new Point(0, 0), this) is { } position)
        {
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, position.Y - barHeight - 4));
        }
    }

    /// <summary>
    /// Folds the document so the headings down to a nesting level show and everything under the headings of that level is hidden.
    /// </summary>
    /// <param name="level">The nesting level, 1 for only the headings of the top level, or 0 to open every section.</param>
    internal void FoldToLevel(int level)
    {
        for (int i = 0; i < collapsed.Count; i++)
        {
            collapsed[i] = level > 0 && sections[i].Depth >= level - 1;
        }

        Fold();
    }

    /// <summary>
    /// Works out which headings the top of the view is under, the ones at or above the breadcrumb bar that lies over its top, and publishes them as <see cref="Trail" />.
    /// </summary>
    /// <param name="offset">How far the view is scrolled down.</param>
    internal void UpdateTrail(double offset)
    {
        List<MarkdownHeading> trail = [];

        if (offset >= 1)
        {
            int found = -1;

            for (int i = 0; i < sections.Count; i++)
            {
                if (!sections[i].Head.IsEffectivelyVisible || sections[i].Head.TranslatePoint(new Point(0, 0), this) is not { } position)
                {
                    continue;
                }

                if (position.Y >= offset + barHeight)
                {
                    break;
                }

                found = i;
            }

            for (int at = found; at >= 0; at = sections[at].Parent)
            {
                trail.Insert(0, headings[at]);
            }
        }

        if (!trail.Select(item => item.Index).SequenceEqual(Trail.Select(item => item.Index)))
        {
            Trail = trail;
        }
    }

    private void Annotate()
    {
        Dictionary<int, int> siblings = [];
        headings.Clear();
        bool titled = sections.Count > 1 && sections.Count(section => section.Parent < 0) == 1;

        for (int i = 0; i < sections.Count; i++)
        {
            Section section = sections[i];
            siblings[section.Parent] = siblings.GetValueOrDefault(section.Parent) + 1;
            string own = siblings[section.Parent].ToString(CultureInfo.InvariantCulture);
            string parentNumber = section.Parent >= 0 ? headings[section.Parent].Number : string.Empty;
            string number = titled && i == 0 ? string.Empty : parentNumber.Length == 0 ? own : $"{parentNumber}.{own}";
            section.NumberLabel.Text = number;
            headings.Add(new MarkdownHeading { Index = i, Title = section.Title, Level = section.Level, Depth = section.Depth, Number = number, Parent = section.Parent });
        }

        ShowNumbers();
        Outline = [.. headings];
        Trail = [];
    }

    private void ShowNumbers()
    {
        foreach (Section section in sections)
        {
            section.NumberLabel.IsVisible = Numbered && !string.IsNullOrEmpty(section.NumberLabel.Text);
        }
    }

    private string TitleOf(HeadingBlock heading) => heading.Inline is null ? string.Empty : string.Concat(heading.Inline.Descendants().Select(node => node switch
    {
        LiteralInline literal => literal.Content.ToString(),
        CodeInline code => code.Content,
        _ => string.Empty,
    })).Trim();

    private void Toggle(int index)
    {
        collapsed[index] = !collapsed[index];
        Fold();
    }

    private Section NewSection(HeadingBlock heading, int parent, ScrollSafeTextBlock title, int index)
    {
        int depth = parent >= 0 ? sections[parent].Depth + 1 : 0;
        int level = Math.Clamp(heading.Level, 1, 6);
        title.FontWeight = weights[level - 1];
        title.Opacity = fades[level - 1];
        Button chevron = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
        chevron.Classes.Add("icon");
        chevron.Click += (_, _) => Toggle(index);
        TextBlock number = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsVisible = Numbered };
        number.Classes.Add("muted");
        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(number, 1);
        Grid.SetColumn(title, 2);
        head.Children.Add(chevron);
        head.Children.Add(number);
        head.Children.Add(title);
        Border ruled = new() { BorderBrush = Brush("Border", Brushes.Gray), BorderThickness = level <= 2 ? new Thickness(0, 0, 0, 1) : default, Padding = level <= 2 ? new Thickness(0, 0, 0, 4) : default, Child = head };
        StackPanel body = new() { Spacing = 10 };
        Border frame = new() { BorderBrush = Brush("Border", Brushes.Gray), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(11, 0, 0, 0), Padding = new Thickness(Math.Max(6, 16 - (2 * depth)), 0, 0, 0), Child = body };
        head.PointerEntered += (_, _) => frame.BorderBrush = Brush("Accent", Brushes.Orange);
        head.PointerExited += (_, _) => frame.BorderBrush = Brush("Border", Brushes.Gray);
        return new Section { Body = body, Frame = frame, Head = head, Chevron = chevron, NumberLabel = number, Parent = parent, Level = level, Depth = depth, Title = TitleOf(heading), Root = new StackPanel { Spacing = 10, Children = { ruled, frame } } };
    }

    private void Begin(ScrollSafeTextBlock target)
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
                    current = (block, position - blockStart, containing);
                }
            }

            target.Add(run);
            at = end;
        }
    }

    private IBrush Brush(string key, IBrush fallback) => Application.Current?.Resources.TryGetResource(key, ActualThemeVariant, out object? value) == true && value is IBrush brush ? brush : fallback;

    private StackPanel Blocks(IEnumerable<Block> blocks, double indent, bool sectioned = false)
    {
        StackPanel panel = new() { Spacing = 10 };
        List<int> open = [];

        foreach (Block block in blocks)
        {
            if (sectioned && block is HeadingBlock heading)
            {
                while (open.Count > 0 && sections[open[^1]].Level >= heading.Level)
                {
                    open.RemoveAt(open.Count - 1);
                }

                int parent = open.Count > 0 ? open[^1] : -1;
                containing = parent;
                int index = sections.Count;
                Section section = NewSection(heading, parent, Heading(heading), index);
                sections.Add(section);
                (parent >= 0 ? sections[parent].Body : panel).Children.Add(section.Root);
                open.Add(index);
                containing = index;
                continue;
            }

            Control? control = Build(block, indent);

            if (control is not null)
            {
                (open.Count > 0 ? sections[open[^1]].Body : panel).Children.Add(control);
            }
        }

        containing = -1;
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

    private ScrollSafeTextBlock Plain(string text, double size)
    {
        ScrollSafeTextBlock result = new() { FontSize = size, TextWrapping = TextWrapping.Wrap };
        Begin(result);
        Emit(result.Inlines!, text);
        End();
        return result;
    }

    private ScrollSafeTextBlock Heading(HeadingBlock heading)
    {
        double[] sizes = [26, 22, 19, 17, 15, 14];
        return Text(heading.Inline, sizes[Math.Clamp(heading.Level, 1, 6) - 1], FontWeight.SemiBold);
    }

    private ScrollSafeTextBlock Text(ContainerInline? inline, double size, FontWeight weight)
    {
        ScrollSafeTextBlock result = new() { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
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
        ScrollSafeTextBlock result = new() { FontFamily = mono, FontSize = 12 };
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
                ScrollSafeTextBlock text = new() { FontSize = 13, FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
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

    private sealed class Section
    {
        public required StackPanel Body { get; init; }

        public required Border Frame { get; init; }

        public required Grid Head { get; init; }

        public required StackPanel Root { get; init; }

        public required Button Chevron { get; init; }

        public required TextBlock NumberLabel { get; init; }

        public required int Parent { get; init; }

        public required int Level { get; init; }

        public required int Depth { get; init; }

        public required string Title { get; init; }
    }
}
