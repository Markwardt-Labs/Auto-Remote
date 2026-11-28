namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A row of three parts: a leading one, one that fills what is left and a trailing one. When too little would be left for the part that fills, the trailing one goes to a line of its own below the other two, so nothing is squeezed out or cut off on a narrow screen.
/// </summary>
public sealed class AdaptiveRow : Panel
{
    static AdaptiveRow() => AffectsMeasure<AdaptiveRow>(MinFillWidthProperty);

    /// <summary>
    /// The <see cref="MinFillWidth" /> property.
    /// </summary>
    public static readonly StyledProperty<double> MinFillWidthProperty = AvaloniaProperty.Register<AdaptiveRow, double>(nameof(MinFillWidth));

    /// <summary>
    /// Gets or sets the least width the part that fills is left with while all three share a line.
    /// </summary>
    public double MinFillWidth
    {
        get => GetValue(MinFillWidthProperty);
        set => SetValue(MinFillWidthProperty, value);
    }

    /// <summary>
    /// Gets a value indicating whether the trailing part is on a line of its own.
    /// </summary>
    public bool IsWrapped { get; private set; }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3)
        {
            return base.MeasureOverride(availableSize);
        }

        Control lead = Children[0];
        Control fill = Children[1];
        Control trail = Children[2];
        Size unbounded = new(double.PositiveInfinity, availableSize.Height);
        lead.Measure(unbounded);
        trail.Measure(unbounded);
        double beside = lead.DesiredSize.Width + trail.DesiredSize.Width;
        IsWrapped = availableSize.Width - beside < MinFillWidth;

        if (!IsWrapped)
        {
            fill.Measure(new Size(Math.Max(0, availableSize.Width - beside), availableSize.Height));
            double width = double.IsPositiveInfinity(availableSize.Width) ? beside + fill.DesiredSize.Width : availableSize.Width;
            return new Size(width, Math.Max(fill.DesiredSize.Height, Math.Max(lead.DesiredSize.Height, trail.DesiredSize.Height)));
        }

        trail.Measure(new Size(availableSize.Width, availableSize.Height));
        fill.Measure(new Size(Math.Max(0, availableSize.Width - lead.DesiredSize.Width), Math.Max(0, availableSize.Height - trail.DesiredSize.Height)));
        return new Size(availableSize.Width, Math.Max(fill.DesiredSize.Height, lead.DesiredSize.Height) + trail.DesiredSize.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 3)
        {
            return base.ArrangeOverride(finalSize);
        }

        Control lead = Children[0];
        Control fill = Children[1];
        Control trail = Children[2];
        double leading = lead.DesiredSize.Width;

        if (!IsWrapped)
        {
            double trailing = trail.DesiredSize.Width;
            lead.Arrange(new Rect(0, 0, leading, finalSize.Height));
            fill.Arrange(new Rect(leading, 0, Math.Max(0, finalSize.Width - leading - trailing), finalSize.Height));
            trail.Arrange(new Rect(Math.Max(0, finalSize.Width - trailing), 0, trailing, finalSize.Height));
            return finalSize;
        }

        double top = Math.Max(0, finalSize.Height - trail.DesiredSize.Height);
        lead.Arrange(new Rect(0, 0, leading, top));
        fill.Arrange(new Rect(leading, 0, Math.Max(0, finalSize.Width - leading), top));
        trail.Arrange(new Rect(0, top, finalSize.Width, trail.DesiredSize.Height));
        return finalSize;
    }
}
