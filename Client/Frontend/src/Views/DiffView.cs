namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Shows a file's diff as one sequence of lines in a monospaced font, with the line numbers before and after the change, and added and removed lines marked.
/// </summary>
internal sealed class DiffView : ContentControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiffView" /> class.
    /// </summary>
    public DiffView()
    {
        before = Gutter();
        after = Gutter();
        block = new ScrollSafeTextBlock { FontFamily = mono, FontSize = 12, Background = Brushes.Transparent };
        scroller = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = block };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        block.LayoutUpdated += (_, _) => NumberLines();
        Grid.SetColumn(after, 1);
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(before);
        grid.Children.Add(after);
        grid.Children.Add(scroller);
        Content = grid;
    }

    /// <summary>
    /// Identifies the <see cref="Diff" /> property.
    /// </summary>
    public static readonly StyledProperty<GitDiff?> DiffProperty = AvaloniaProperty.Register<DiffView, GitDiff?>(nameof(Diff));

    /// <summary>
    /// Identifies the <see cref="Wrap" /> property.
    /// </summary>
    public static readonly StyledProperty<bool> WrapProperty = AvaloniaProperty.Register<DiffView, bool>(nameof(Wrap));

    static DiffView()
    {
        DiffProperty.Changed.AddClassHandler<DiffView>((view, _) => view.Render());
        WrapProperty.Changed.AddClassHandler<DiffView>((view, _) => view.Rewrap());
    }

    private readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly TextBlock before;

    private readonly TextBlock after;

    private readonly SelectableTextBlock block;

    private readonly ScrollViewer scroller;

    private TextLayout? numbered;

    private IReadOnlyList<string> oldNumbers = [];

    private IReadOnlyList<string> newNumbers = [];

    private string text = string.Empty;

    /// <summary>
    /// Gets or sets the diff to show.
    /// </summary>
    public GitDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether long lines wrap instead of scrolling sideways.
    /// </summary>
    public bool Wrap
    {
        get => GetValue(WrapProperty);
        set => SetValue(WrapProperty, value);
    }

    private TextBlock Gutter() => new() { FontFamily = mono, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false };

    private IBrush? Brush(string key) => Application.Current?.Resources.TryGetResource(key, ActualThemeVariant, out object? value) == true ? value as IBrush : null;

    private void Render()
    {
        block.Inlines!.Clear();
        block.Text = null;
        oldNumbers = [];
        newNumbers = [];
        text = string.Empty;
        NumberLines();
        before.Foreground = Brush("LineNumber");
        after.Foreground = Brush("LineNumber");

        if (Diff is not { } diff)
        {
            return;
        }

        if (diff.IsBinary)
        {
            block.Text = "Binary file.";
            return;
        }

        if (diff.Lines.Count == 0)
        {
            block.Text = "No changes.";
            return;
        }

        oldNumbers = [.. diff.Lines.Select(line => line.OldNumber?.ToString(CultureInfo.InvariantCulture) ?? " ")];
        newNumbers = [.. diff.Lines.Select(line => line.NewNumber?.ToString(CultureInfo.InvariantCulture) ?? " ")];
        text = string.Concat(diff.Lines.Select((line, i) => "  " + line.Text + (i < diff.Lines.Count - 1 ? "\n" : string.Empty)));
        NumberLines();
        IBrush? added = Brush("DiffAdded");
        IBrush? removed = Brush("DiffRemoved");

        for (int i = 0; i < diff.Lines.Count; i++)
        {
            GitDiffLine line = diff.Lines[i];
            string marker = line.Kind switch { GitDiffKind.Added => "+ ", GitDiffKind.Removed => "- ", _ => "  " };
            block.Inlines.Add(new Run(marker + line.Text + (i < diff.Lines.Count - 1 ? "\n" : string.Empty)) { Background = line.Kind switch { GitDiffKind.Added => added, GitDiffKind.Removed => removed, _ => null } });
        }

        if (diff.Truncated)
        {
            block.Inlines.Add(new Run("\n(the rest of the file is not shown)") { Foreground = Brush("Muted") });
        }
    }

    private void Rewrap()
    {
        block.TextWrapping = Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        scroller.HorizontalScrollBarVisibility = Wrap ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        NumberLines();
    }

    private void NumberLines()
    {
        if (!Wrap || block.TextLayout is not { } layout || oldNumbers.Count == 0)
        {
            numbered = null;
            before.Text = string.Join('\n', oldNumbers);
            after.Text = string.Join('\n', newNumbers);
            return;
        }

        if (ReferenceEquals(layout, numbered))
        {
            return;
        }

        numbered = layout;

        StringBuilder olds = new();
        StringBuilder news = new();
        int number = -1;

        foreach (TextLine line in layout.TextLines)
        {
            int start = line.FirstTextSourceIndex;

            if (start == 0 || (start <= text.Length && text[start - 1] == '\n'))
            {
                number++;
            }
            else
            {
                olds.Append('\n');
                news.Append('\n');
                continue;
            }

            olds.Append(number < oldNumbers.Count ? oldNumbers[number] : " ").Append('\n');
            news.Append(number < newNumbers.Count ? newNumbers[number] : " ").Append('\n');
        }

        before.Text = olds.ToString().TrimEnd('\n');
        after.Text = news.ToString().TrimEnd('\n');
    }
}
