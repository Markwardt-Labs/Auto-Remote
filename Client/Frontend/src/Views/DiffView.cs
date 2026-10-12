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
        block = new SelectableTextBlock { FontFamily = mono, FontSize = 12 };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        ScrollViewer scroller = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = block };
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

    static DiffView() => DiffProperty.Changed.AddClassHandler<DiffView>((view, _) => view.Render());

    private readonly FontFamily mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly TextBlock before;

    private readonly TextBlock after;

    private readonly SelectableTextBlock block;

    /// <summary>
    /// Gets or sets the diff to show.
    /// </summary>
    public GitDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    private TextBlock Gutter() => new() { FontFamily = mono, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false };

    private IBrush? Brush(string key) => Application.Current?.Resources.TryGetResource(key, ActualThemeVariant, out object? value) == true ? value as IBrush : null;

    private void Render()
    {
        block.Inlines!.Clear();
        block.Text = null;
        before.Text = string.Empty;
        after.Text = string.Empty;
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

        before.Text = string.Join('\n', diff.Lines.Select(line => line.OldNumber?.ToString(CultureInfo.InvariantCulture) ?? " "));
        after.Text = string.Join('\n', diff.Lines.Select(line => line.NewNumber?.ToString(CultureInfo.InvariantCulture) ?? " "));
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
}
