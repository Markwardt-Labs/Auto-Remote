namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Draws the branch graph beside one commit of the history: the node, the lines of the branches through its row and, when taller than the row, the branches carrying on below it.
/// </summary>
internal sealed class GitGraphView : Control
{
    /// <summary>
    /// Identifies the <see cref="Row" /> property.
    /// </summary>
    public static readonly StyledProperty<GitGraphRow?> RowProperty = AvaloniaProperty.Register<GitGraphView, GitGraphRow?>(nameof(Row));

    /// <summary>
    /// Identifies the <see cref="Lanes" /> property.
    /// </summary>
    public static readonly StyledProperty<int> LanesProperty = AvaloniaProperty.Register<GitGraphView, int>(nameof(Lanes), 1);

    static GitGraphView()
    {
        AffectsRender<GitGraphView>(RowProperty, LanesProperty);
        AffectsMeasure<GitGraphView>(LanesProperty);
    }

    private readonly double rowHeight = 28;

    private readonly double laneWidth = 14;

    private readonly double margin = 6;

    private readonly GitGraphColors colors = new();

    /// <summary>
    /// Gets or sets the graph of the commit.
    /// </summary>
    public GitGraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>
    /// Gets or sets how many lanes the whole history needs, which keeps every row the same width.
    /// </summary>
    public int Lanes
    {
        get => GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(Lanes, 1) * laneWidth + margin * 2, 0);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
        {
            return;
        }

        foreach (GitGraphLine line in row.Lines)
        {
            Draw(context, line.FromLane, line.FromY * rowHeight, line.ToLane, line.ToY * rowHeight, line.Color);
        }

        foreach ((int lane, int color) in row.Continuing)
        {
            if (Bounds.Height > rowHeight)
            {
                Draw(context, lane, rowHeight, lane, Bounds.Height, color);
            }
        }

        IBrush brush = new SolidColorBrush(colors.At(row.Color));
        context.DrawEllipse(brush, new Pen(Application.Current?.Resources.TryGetResource("Background", ActualThemeVariant, out object? background) == true ? background as IBrush : Brushes.Black, 1.5), new Point(X(row.Lane), rowHeight / 2), 4.5, 4.5);
    }

    private double X(int lane) => margin + lane * laneWidth + laneWidth / 2;

    private void Draw(DrawingContext context, int fromLane, double fromY, int toLane, double toY, int color)
    {
        Pen pen = new(new SolidColorBrush(colors.At(color)), 2, lineCap: PenLineCap.Round);

        if (fromLane == toLane)
        {
            context.DrawLine(pen, new Point(X(fromLane), fromY), new Point(X(toLane), toY));
            return;
        }

        StreamGeometry geometry = new();

        using (StreamGeometryContext path = geometry.Open())
        {
            double middle = (fromY + toY) / 2;
            path.BeginFigure(new Point(X(fromLane), fromY), false);
            path.CubicBezierTo(new Point(X(fromLane), middle), new Point(X(toLane), middle), new Point(X(toLane), toY));
            path.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
