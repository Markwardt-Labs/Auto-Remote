namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Selectable text that does not start selecting when it is touched to scroll the list it sits in: a touch, however many times it is repeated, only starts a selection (of the word under the finger) once the finger is held still for <see cref="HoldDelay" />, and a touch that moves first stays a scroll. A selection made that way gets handles that drag out its ends and a bar to copy it, and lasts until a tap elsewhere. A mouse and a pen select at once as usual.
/// </summary>
public sealed class ScrollSafeTextBlock : SelectableTextBlock
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ScrollSafeTextBlock" /> class.
    /// </summary>
    public ScrollSafeTextBlock()
    {
        // Without a background only what is drawn can be touched, which leaves the gaps between lines and past their ends out.
        Background = Brushes.Transparent;

        // Holding a finger also asks for the context menu, whose place the bar of the touch selection takes.
        AddHandler(ContextRequestedEvent, (_, args) => args.Handled |= touched, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private readonly double slop = 10;

    private IDisposable? hold;

    private Avalonia.Input.PointerPressedEventArgs? pressed;

    private Avalonia.Point start;

    private bool selecting;

    private bool dragging;

    private bool touched;

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
        touched = e.Pointer.Type == Avalonia.Input.PointerType.Touch;

        if (touched)
        {
            selecting = false;
            dragging = false;
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

        if (e.Pointer.Type != Avalonia.Input.PointerType.Touch)
        {
            base.OnPointerMoved(e);
        }
        else if (selecting && TouchSelection.Covers(this))
        {
            // The list must not take the finger for a scroll now that it is selecting.
            e.PreventGestureRecognition();
            e.Handled = true;
            dragging |= Math.Abs(e.GetPosition(this).X - start.X) > slop || Math.Abs(e.GetPosition(this).Y - start.Y) > slop;

            if (dragging)
            {
                TouchSelection.Reach(e);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        Stop();

        if (e.Pointer.Type != Avalonia.Input.PointerType.Touch || selecting)
        {
            base.OnPointerReleased(e);
        }

        if (selecting)
        {
            TouchSelection.Lift();
        }

        selecting = false;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        if (pressed is not null && e.Pointer == pressed.Pointer && hold is not null)
        {
            Stop();
        }

        if (selecting)
        {
            TouchSelection.Lift();
        }

        selecting = false;

        base.OnPointerCaptureLost(e);
    }

    /// <inheritdoc />
    protected override void OnLostFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        // The text control drops its selection when it loses the focus, which a finger scrolling or dragging a handle can take from it.
        if (!TouchSelection.Covers(this))
        {
            base.OnLostFocus(e);
        }
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

        selecting = true;

        // Selecting a word leaves an end where it was when the finger is already at that end of the word, so both are first brought to the finger.
        int under = TextLayout.HitTestPoint(original.GetPosition(this) - new Avalonia.Point(Padding.Left, Padding.Top)).TextPosition;
        SetCurrentValue(SelectionStartProperty, under);
        SetCurrentValue(SelectionEndProperty, under);
        UpdateLayout();
        base.OnPointerPressed(new Avalonia.Input.PointerPressedEventArgs(this, original.Pointer, root, original.GetPosition(root), original.Timestamp, original.GetCurrentPoint(root).Properties, original.KeyModifiers, 2));
        TouchSelection.Start(this);
    }
}
