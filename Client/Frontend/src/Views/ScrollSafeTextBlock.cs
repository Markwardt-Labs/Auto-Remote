namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Selectable text that does not start selecting when it is touched to scroll the list it sits in: a touch, however many times it is repeated, only starts a selection (of the word under the finger, which can then be dragged out) once the finger is held still for <see cref="HoldDelay" />, and a touch that moves first stays a scroll. A touch anywhere in such text drops the selection a previous hold made. A mouse and a pen select at once as usual.
/// </summary>
public sealed class ScrollSafeTextBlock : SelectableTextBlock
{
    private static ScrollSafeTextBlock? selected;

    private readonly double slop = 10;

    private IDisposable? hold;

    private Avalonia.Input.PointerPressedEventArgs? pressed;

    private Avalonia.Point start;

    /// <summary>
    /// Gets or sets how long a touch has to stay still before it starts selecting.
    /// </summary>
    public TimeSpan HoldDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <inheritdoc />
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        Stop();

        if (e.Pointer.Type == Avalonia.Input.PointerType.Touch)
        {
            selected?.ClearSelection();
            selected = null;
            pressed = e;
            start = e.GetPosition(this);
            hold = Avalonia.Threading.DispatcherTimer.RunOnce(Select, HoldDelay);
            return;
        }

        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        if (pressed is not null && (Math.Abs(e.GetPosition(this).X - start.X) > slop || Math.Abs(e.GetPosition(this).Y - start.Y) > slop))
        {
            Stop();
        }

        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        Stop();
        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        if (pressed is not null && e.Pointer == pressed.Pointer && hold is not null)
        {
            Stop();
        }

        base.OnPointerCaptureLost(e);
    }

    private void Stop()
    {
        hold?.Dispose();
        hold = null;
        pressed = null;
    }

    private void Select()
    {
        Avalonia.Input.PointerPressedEventArgs? original = pressed;
        Stop();

        if (original is null || original.Pointer.Captured is { } other && other != this && !ReferenceEquals(other, original.Source) || TopLevel.GetTopLevel(this) is not { } root)
        {
            return;
        }

        selected = this;
        base.OnPointerPressed(new Avalonia.Input.PointerPressedEventArgs(this, original.Pointer, root, original.GetPosition(root), original.Timestamp, original.GetCurrentPoint(root).Properties, original.KeyModifiers, 2));
    }
}
