namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A button that leaves a touch to the scrolling list it sits in until the touch proves to be a tap: it does not capture the finger when it lands, so dragging from it scrolls, and it clicks when the finger lifts without having moved. A mouse and a pen click as usual. A button with an <see cref="OptionsCommand" /> offers more than its click: a right click, the keyboard's menu key or a finger held still on it run that command instead.
/// </summary>
public sealed class TouchButton : Button
{
    /// <summary>
    /// The <see cref="OptionsCommand" /> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> OptionsCommandProperty = AvaloniaProperty.Register<TouchButton, ICommand?>(nameof(OptionsCommand));

    /// <summary>
    /// The <see cref="OptionsParameter" /> property.
    /// </summary>
    public static readonly StyledProperty<object?> OptionsParameterProperty = AvaloniaProperty.Register<TouchButton, object?>(nameof(OptionsParameter));

    /// <summary>
    /// Initializes a new instance of the <see cref="TouchButton" /> class.
    /// </summary>
    public TouchButton() => AddHandler(ContextRequestedEvent, OnOptionsRequested);

    private readonly double slop = 10;

    private CancellationTokenSource? holding;

    private bool held;

    private Avalonia.Input.IPointer? touch;

    private Avalonia.Point start;

    /// <summary>
    /// Gets or sets the command a right click, the menu key or a held finger runs instead of the click, or <see langword="null" /> when the button offers nothing more.
    /// </summary>
    public ICommand? OptionsCommand
    {
        get => GetValue(OptionsCommandProperty);
        set => SetValue(OptionsCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets what <see cref="OptionsCommand" /> is run with.
    /// </summary>
    public object? OptionsParameter
    {
        get => GetValue(OptionsParameterProperty);
        set => SetValue(OptionsParameterProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>
    /// Gets or sets how long a finger is held still on the button before it counts as held.
    /// </summary>
    internal TimeSpan HoldDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private bool CanRunOptions => IsEffectivelyEnabled && IsEffectivelyVisible && OptionsCommand?.CanExecute(OptionsParameter) == true;

    /// <inheritdoc />
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        held = false;

        if (e.Pointer.Type != Avalonia.Input.PointerType.Touch || !IsEffectivelyEnabled)
        {
            base.OnPointerPressed(e);
            return;
        }

        touch = e.Pointer;
        start = e.GetPosition(this);
        PseudoClasses.Set(":pressed", true);

        if (OptionsCommand is not null)
        {
            CancellationTokenSource source = new();
            holding = source;
            _ = RunAfterHold(source.Token);
        }
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
            // A finger that was held has had its options, and lifting it does not click as well.
            bool tapped = !held && Math.Abs(e.GetPosition(this).X - start.X) <= slop && Math.Abs(e.GetPosition(this).Y - start.Y) <= slop;
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

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndHold();
        base.OnDetachedFromVisualTree(e);
    }

    private void Release()
    {
        EndHold();
        touch = null;
        PseudoClasses.Set(":pressed", false);
    }

    private void EndHold()
    {
        holding?.Cancel();
        holding = null;
    }

    private async Task RunAfterHold(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(HoldDelay, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        holding = null;

        // With no options to give, the finger is still only on its way to a tap.
        if (CanRunOptions)
        {
            held = true;
            PseudoClasses.Set(":pressed", false);
            RunOptions();
        }
    }

    // Asked for by a right click and the menu key; a platform may ask for it for a held finger too, which this button times itself.
    private void OnOptionsRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        if (OptionsCommand is null)
        {
            return;
        }

        e.Handled = true;

        if (touch is null && !held)
        {
            RunOptions();
        }
    }

    private void RunOptions()
    {
        if (CanRunOptions)
        {
            OptionsCommand!.Execute(OptionsParameter);
        }
    }
}
