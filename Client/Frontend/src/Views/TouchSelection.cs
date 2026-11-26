namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The selection a touch made. It has a handle at each end, which drags that end across all the text blocks of a formatted message (or within the one block elsewhere) and scrolls when taken to an edge, and a bar that copies the text or selects all of it. One exists at a time: a tap anywhere else ends it, and scrolling keeps it.
/// </summary>
internal sealed class TouchSelection
{
    private static TouchSelection? current;

    /// <summary>
    /// Takes over the selection a hold just made in a block, ending any other; nothing starts when the block has nothing selected.
    /// </summary>
    /// <param name="origin">The block that was held.</param>
    public static void Start(ScrollSafeTextBlock origin)
    {
        // The word just held may be in a message that had the selection before, which must not clear it again.
        current?.Dismiss(origin);

        if (origin.SelectionStart != origin.SelectionEnd && TopLevel.GetTopLevel(origin) is { } root && Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(origin) is { } layer)
        {
            current = new TouchSelection(origin, root, layer);
        }
    }

    /// <summary>
    /// Stretches the selection from the held word to the finger that held it, while it is dragged on.
    /// </summary>
    /// <param name="args">The movement of the finger.</param>
    public static void Reach(Avalonia.Input.PointerEventArgs args) => current?.Drag(null, args.GetPosition(current.layer));

    /// <summary>
    /// Ends the dragging of the finger that held the word.
    /// </summary>
    public static void Lift() => current?.Settle();

    /// <summary>
    /// Tells whether a block is one the selection can reach, so it must keep what it has selected when it loses the focus.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public static bool Covers(ScrollSafeTextBlock block) => current?.blocks.Contains(block) == true;

    private TouchSelection(ScrollSafeTextBlock origin, TopLevel root, Avalonia.Controls.Primitives.OverlayLayer layer)
    {
        this.root = root;
        this.layer = layer;
        blocks = origin.FindAncestorOfType<MarkdownView>() is { } message ? [.. message.GetVisualDescendants().OfType<ScrollSafeTextBlock>().Where(block => block.IsEffectivelyVisible)] : [origin];
        Button copy = new() { Content = "Copy", Focusable = false };
        Button all = new() { Content = "Select All", Focusable = false };
        copy.Classes.Add("flat");
        all.Classes.Add("flat");
        copy.Click += async (_, _) => await Copy();
        all.Click += (_, _) => SelectAll();
        bar = new Border
        {
            Name = "SelectionBar",
            Background = Brush("Elevated"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { copy, all } },
        };
        scrolling = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        scrolling.Tick += (_, _) => ScrollTowardsFinger();

        foreach (SelectionHandle handle in new[] { leading, trailing })
        {
            // The tip is at the bottom of a line; the text meant is in the middle of it.
            handle.Dragged += tip => Drag(handle, new Avalonia.Point(tip.X, tip.Y - (lineHeight / 2)));
            handle.Released += Settle;
            layer.Children.Add(handle);
        }

        layer.Children.Add(bar);
        root.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        root.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        root.LayoutUpdated += OnLayoutUpdated;
        Read(origin);
    }

    private readonly TopLevel root;

    private readonly Avalonia.Controls.Primitives.OverlayLayer layer;

    private readonly List<ScrollSafeTextBlock> blocks;

    private readonly SelectionHandle leading = new(true);

    private readonly SelectionHandle trailing = new(false);

    private readonly Border bar;

    private readonly DispatcherTimer scrolling;

    private readonly double slop = 10;

    private readonly double edge = 36;

    private readonly double step = 14;

    private (int Block, int Index) first;

    private (int Block, int Index) last;

    private ((int Block, int Index) First, (int Block, int Index) Last) held;

    private (Avalonia.Input.IPointer Pointer, Avalonia.Point At)? tap;

    private (SelectionHandle? Handle, Avalonia.Point Finger)? drag;

    private double lineHeight = 20;

    private void Read(ScrollSafeTextBlock origin)
    {
        int block = blocks.IndexOf(origin);

        if (block >= 0 && origin.SelectionStart != origin.SelectionEnd)
        {
            first = (block, Math.Min(origin.SelectionStart, origin.SelectionEnd));
            last = (block, Math.Max(origin.SelectionStart, origin.SelectionEnd));
            held = (first, last);
            Apply();
            Place();
        }
    }

    private string TextOf(ScrollSafeTextBlock block) => (block.Inlines is { Count: > 0 } inlines ? inlines.Text : block.Text) ?? string.Empty;

    private int Length(int block) => TextOf(blocks[block]).Length;

    private void Apply()
    {
        for (int index = 0; index < blocks.Count; index++)
        {
            bool inside = index >= first.Block && index <= last.Block;
            int from = inside && index == first.Block ? first.Index : 0;
            int to = !inside ? 0 : index == last.Block ? last.Index : Length(index);
            blocks[index].SetCurrentValue(SelectableTextBlock.SelectionStartProperty, from);
            blocks[index].SetCurrentValue(SelectableTextBlock.SelectionEndProperty, to);
        }
    }

    // Without a handle it is the finger that held the word, which keeps that word selected whichever way it goes.
    private void Drag(SelectionHandle? handle, Avalonia.Point finger)
    {
        drag = (handle, finger);
        Extend();
        scrolling.Start();
    }

    private void Extend()
    {
        if (drag is not { } dragged)
        {
            return;
        }

        (SelectionHandle? handle, Avalonia.Point finger) = dragged;

        (int Block, int Index) spot = Find(Inside(finger));

        // The ends stop one character short of each other rather than cross.
        if (handle == leading)
        {
            first = spot.CompareTo(last) < 0 ? spot : (last.Block, last.Index - 1);
        }
        else if (handle == trailing)
        {
            last = spot.CompareTo(first) > 0 ? spot : (first.Block, first.Index + 1);
        }
        else
        {
            first = spot.CompareTo(held.First) < 0 ? spot : held.First;
            last = spot.CompareTo(held.Last) > 0 ? spot : held.Last;
        }

        // An end that only touches the edge of a block belongs to the text next to it.
        while (first.Index >= Length(first.Block) && first.Block < last.Block)
        {
            first = (first.Block + 1, 0);
        }

        while (last.Index <= 0 && last.Block > first.Block)
        {
            last = (last.Block - 1, Length(last.Block - 1));
        }

        Apply();
        Place();
    }

    // The place in the text nearest to a point of the layer: in the block closest to it, up and down counting first.
    private (int Block, int Index) Find(Avalonia.Point point)
    {
        (double Below, double Beside, int Block) best = (double.MaxValue, double.MaxValue, first.Block);

        for (int index = 0; index < blocks.Count; index++)
        {
            if (Length(index) == 0 || blocks[index].TranslatePoint(default, layer) is not { } corner)
            {
                continue;
            }

            Rect area = new(corner, blocks[index].Bounds.Size);
            (double, double, int) candidate = (Math.Max(0, Math.Max(area.Top - point.Y, point.Y - area.Bottom)), Math.Max(0, Math.Max(area.Left - point.X, point.X - area.Right)), index);

            if (candidate.CompareTo(best) < 0)
            {
                best = candidate;
            }
        }

        ScrollSafeTextBlock block = blocks[best.Block];
        Avalonia.Point local = (layer.TranslatePoint(point, block) ?? default) - new Avalonia.Point(block.Padding.Left, block.Padding.Top);

        if (local.Y < 0)
        {
            return (best.Block, 0);
        }

        if (local.Y > block.TextLayout.Height)
        {
            return (best.Block, Length(best.Block));
        }

        return (best.Block, Math.Clamp(block.TextLayout.HitTestPoint(local).TextPosition, 0, Length(best.Block)));
    }

    private void Settle()
    {
        if (drag is null)
        {
            return;
        }

        drag = null;
        scrolling.Stop();
        Place();
    }

    private ScrollViewer? Scroller() => blocks[first.Block].GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(scroller => scroller.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

    private Rect AreaOf(Visual visual) => visual.TranslatePoint(default, layer) is { } corner ? new Rect(corner, visual.Bounds.Size) : default;

    // The part of the screen the text scrolls in.
    private Rect Viewport() => Scroller() is { } scroller ? AreaOf(scroller) : new Rect(layer.Bounds.Size);

    // A finger beyond the text that shows means its edge, not text that is scrolled out of sight.
    private Avalonia.Point Inside(Avalonia.Point point)
    {
        Rect view = Viewport();
        return new Avalonia.Point(point.X, Math.Clamp(point.Y, view.Top + 1, Math.Max(view.Top + 1, view.Bottom - 1)));
    }

    private void ScrollTowardsFinger()
    {
        if (drag is not { } dragged)
        {
            return;
        }

        (SelectionHandle? handle, Avalonia.Point finger) = dragged;

        bool toStart = handle == leading || handle is null && Find(Inside(finger)).CompareTo(held.First) < 0;
        bool moved = false;

        // The chat scrolls up and down, and wide code and tables inside it sideways.
        foreach (ScrollViewer scroller in blocks[toStart ? first.Block : last.Block].GetVisualAncestors().OfType<ScrollViewer>())
        {
            Rect area = AreaOf(scroller);
            double across = scroller.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled ? 0 : finger.X < area.Left + edge ? -step : finger.X > area.Right - edge ? step : 0;
            double down = scroller.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled ? 0 : finger.Y < area.Top + edge ? -step : finger.Y > area.Bottom - edge ? step : 0;
            Vector next = new(Math.Clamp(scroller.Offset.X + across, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)), Math.Clamp(scroller.Offset.Y + down, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));

            if (next != scroller.Offset)
            {
                scroller.Offset = next;
                moved = true;
            }
        }

        if (moved)
        {
            root.UpdateLayout();
            Extend();
        }
    }

    // Where the tip of a handle belongs, and whether that place is in sight.
    private (Avalonia.Point Tip, bool Visible) EndOf(bool start)
    {
        int index = start ? first.Block : last.Block;
        ScrollSafeTextBlock block = blocks[index];
        int from = index == first.Block ? first.Index : 0;
        int to = index == last.Block ? last.Index : Length(index);
        List<Rect> lines = [.. block.TextLayout.HitTestTextRange(from, Math.Max(1, to - from))];
        Rect line = lines.Count > 0 ? (start ? lines[0] : lines[^1]) : block.TextLayout.HitTestTextPosition(start ? from : to);
        lineHeight = Math.Max(12, line.Height);
        Avalonia.Point local = new((start ? line.Left : line.Right) + block.Padding.Left, line.Bottom + block.Padding.Top);
        Avalonia.Point tip = block.TranslatePoint(local, layer) ?? default;
        Avalonia.Point middle = new(tip.X, tip.Y - (line.Height / 2));
        return (tip, block.GetVisualAncestors().OfType<ScrollViewer>().All(scroller => AreaOf(scroller).Inflate(2).Contains(middle)));
    }

    private void Place()
    {
        // The text it was made in may be gone (the message was rendered again), out of sight altogether (another tab) or replaced by a shorter one (a script run anew).
        if (blocks.Skip(first.Block).Take(last.Block - first.Block + 1).Any(block => TopLevel.GetTopLevel(block) != root || !block.IsEffectivelyVisible) || first.Index >= Length(first.Block) || last.Index > Length(last.Block))
        {
            Dismiss();
            return;
        }

        (Avalonia.Point top, bool topVisible) = EndOf(true);
        double topLine = lineHeight;
        (Avalonia.Point bottom, bool bottomVisible) = EndOf(false);

        foreach ((SelectionHandle handle, Avalonia.Point tip, bool visible) in new[] { (leading, top, topVisible), (trailing, bottom, bottomVisible) })
        {
            // A handle that is being dragged stays, or the finger would lose it where the text scrolls under an edge.
            handle.IsVisible = visible || handle.IsDragging;
            Canvas.SetLeft(handle, tip.X - handle.Tip);
            Canvas.SetTop(handle, tip.Y);
        }

        Rect view = Viewport();
        bar.IsVisible = drag is null && bottom.Y > view.Top && top.Y - topLine < view.Bottom;

        if (!bar.IsVisible)
        {
            return;
        }

        bar.Measure(Size.Infinity);
        Size size = bar.DesiredSize;
        double y = top.Y - topLine - size.Height - 8;

        // Above the selection, below it when there is no room, and over its top when neither has room.
        if (y < view.Top)
        {
            double below = bottom.Y + trailing.Height + 4;
            y = below + size.Height <= view.Bottom ? Math.Max(view.Top, below) : view.Top + 8;
        }

        double middle = topVisible && bottomVisible ? (top.X + bottom.X) / 2 : view.Center.X;
        Canvas.SetLeft(bar, Math.Clamp(middle - (size.Width / 2), 4, Math.Max(4, layer.Bounds.Width - size.Width - 4)));
        Canvas.SetTop(bar, y);
    }

    private void SelectAll()
    {
        first = (0, 0);
        last = (blocks.Count - 1, Length(blocks.Count - 1));
        Apply();
        Place();
    }

    private async Task Copy()
    {
        string text = string.Join('\n', blocks.Select(block => block.SelectedText).Where(part => part.Length > 0));
        Avalonia.Input.Platform.IClipboard? clipboard = root.Clipboard;
        Dismiss();

        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private Avalonia.Media.IBrush? Brush(string key) => Application.Current?.Resources.TryGetResource(key, root.ActualThemeVariant, out object? found) == true ? found as IBrush : null;

    private bool IsOwn(object? source) => source is Visual visual && visual.GetSelfAndVisualAncestors().Any(item => item == leading || item == trailing || item == bar);

    private void OnPressed(object? sender, Avalonia.Input.PointerPressedEventArgs args)
    {
        tap = null;

        if (IsOwn(args.Source))
        {
            return;
        }

        // A mouse selects on its own as soon as it is pressed.
        if (args.Pointer.Type == Avalonia.Input.PointerType.Mouse)
        {
            Dismiss();
            return;
        }

        tap = (args.Pointer, args.GetPosition(root));
    }

    // Only a tap ends the selection: a finger that moved was scrolling, perhaps to reach where an end should go.
    private void OnReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs args)
    {
        if (tap is { } pressed && pressed.Pointer == args.Pointer && Math.Abs(args.GetPosition(root).X - pressed.At.X) <= slop && Math.Abs(args.GetPosition(root).Y - pressed.At.Y) <= slop)
        {
            Dismiss();
        }

        tap = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs args) => Place();

    private void Dismiss(ScrollSafeTextBlock? keeping = null)
    {
        if (current != this)
        {
            return;
        }

        current = null;
        drag = null;
        scrolling.Stop();
        root.RemoveHandler(Avalonia.Input.InputElement.PointerPressedEvent, OnPressed);
        root.RemoveHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnReleased);
        root.LayoutUpdated -= OnLayoutUpdated;
        layer.Children.Remove(leading);
        layer.Children.Remove(trailing);
        layer.Children.Remove(bar);

        foreach (ScrollSafeTextBlock block in blocks.Where(block => block != keeping))
        {
            block.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, 0);
            block.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, 0);
        }
    }
}
