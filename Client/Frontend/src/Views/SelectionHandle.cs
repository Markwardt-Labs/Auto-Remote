namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The drop shaped grip at one end of a touch selection. Its tip touches the bottom of the line at that end and the drop hangs outward from the selection; the area that can be touched is larger than the drop.
/// </summary>
internal sealed class SelectionHandle : Control
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SelectionHandle" /> class.
    /// </summary>
    /// <param name="leading">Whether it is the handle at the start of the selection rather than the one at its end.</param>
    public SelectionHandle(bool leading)
    {
        this.leading = leading;
        Width = 44;
        Height = 40;
        Focusable = false;
    }

    private readonly bool leading;

    private readonly double radius = 11;

    private Avalonia.Point grip;

    /// <summary>
    /// Raised while the handle is dragged, with where its tip would be, in the coordinates of its parent.
    /// </summary>
    public event Action<Avalonia.Point>? Dragged;

    /// <summary>
    /// Raised once a drag ends.
    /// </summary>
    public event Action? Released;

    /// <summary>
    /// Gets a value indicating whether a finger is on the handle.
    /// </summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// Gets how far the tip is from the left edge of the handle.
    /// </summary>
    public double Tip => leading ? 3 * radius : radius;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        // Only what is drawn can be touched.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        IBrush brush = Application.Current?.Resources.TryGetResource("Accent", ActualThemeVariant, out object? found) == true && found is IBrush accent ? accent : Brushes.DodgerBlue;
        double side = leading ? -radius : radius;
        context.FillRectangle(brush, new Rect(Math.Min(Tip, Tip + side), 0, radius, radius));
        context.DrawEllipse(brush, null, new Avalonia.Point(Tip + side, radius), radius, radius);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (Parent is Visual parent)
        {
            // The finger keeps the same place on the handle all through the drag, so the tip does not jump to it.
            grip = new Avalonia.Point(Canvas.GetLeft(this) + Tip, Canvas.GetTop(this)) - e.GetPosition(parent);
            IsDragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        if (IsDragging && e.Pointer.Captured == this && Parent is Visual parent)
        {
            Dragged?.Invoke(e.GetPosition(parent) + grip);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        e.Handled = true;
        Release();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e) => Release();

    private void Release()
    {
        if (IsDragging)
        {
            IsDragging = false;
            Released?.Invoke();
        }
    }
}
