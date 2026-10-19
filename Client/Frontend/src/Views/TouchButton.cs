namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A button that leaves a touch to the scrolling list it sits in until the touch proves to be a tap: it does not capture the finger when it lands, so dragging from it scrolls, and it clicks when the finger lifts without having moved. A mouse and a pen click as usual.
/// </summary>
public sealed class TouchButton : Button
{
    private readonly double slop = 10;

    private Avalonia.Input.IPointer? touch;

    private Avalonia.Point start;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <inheritdoc />
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != Avalonia.Input.PointerType.Touch || !IsEffectivelyEnabled)
        {
            base.OnPointerPressed(e);
            return;
        }

        touch = e.Pointer;
        start = e.GetPosition(this);
        PseudoClasses.Set(":pressed", true);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        if (touch is not null && e.Pointer == touch && (Math.Abs(e.GetPosition(this).X - start.X) > slop || Math.Abs(e.GetPosition(this).Y - start.Y) > slop))
        {
            Release();
        }

        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (touch is not null && e.Pointer == touch)
        {
            bool tapped = Math.Abs(e.GetPosition(this).X - start.X) <= slop && Math.Abs(e.GetPosition(this).Y - start.Y) <= slop;
            Release();

            if (tapped)
            {
                OnClick();
            }

            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        if (touch is not null && e.Pointer == touch)
        {
            Release();
        }

        base.OnPointerCaptureLost(e);
    }

    private void Release()
    {
        touch = null;
        PseudoClasses.Set(":pressed", false);
    }
}
