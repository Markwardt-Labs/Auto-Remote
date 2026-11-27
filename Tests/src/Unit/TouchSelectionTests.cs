namespace Markwardt.AutoRemote.Tests.Unit;

[Collection("Headless")]
public sealed class TouchSelectionTests(HeadlessFixture fixture)
{
    private readonly string message = "Alpha bravo charlie.\n\nDelta echo foxtrot.\n\n- Golf hotel\n- India juliet\n\n```\nkilo lima\nmike november\n```\n\nOscar papa quebec.";

    // A chat like list on a phone sized screen: one formatted message with room to scroll under it.
    private sealed class Screen : IDisposable
    {
        private readonly object device;

        private readonly object impl;

        private ulong time;

        public Screen(string source, double below = 0)
            : this(new ScrollViewer { Content = new StackPanel { Margin = new Avalonia.Thickness(16, 80, 16, 16), Children = { new MarkdownView { Source = source }, new Border { Height = below } } } })
        {
        }

        public Screen(Control content)
        {
            Window = new Window { Width = 390, Height = 600, Content = content };
            Window.Show();
            device = Activator.CreateInstance(Raw("Avalonia.Input.TouchDevice"))!;
            impl = typeof(TopLevel).GetProperty("PlatformImpl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(Window)!;
            Settle();
        }

        public Window Window { get; }

        public MarkdownView View => Window.GetVisualDescendants().OfType<MarkdownView>().First(view => view.IsEffectivelyVisible);

        public ScrollViewer Scroller => View.GetVisualAncestors().OfType<ScrollViewer>().First();

        public List<ScrollSafeTextBlock> Blocks => [.. View.GetVisualDescendants().OfType<ScrollSafeTextBlock>()];

        public List<SelectionHandle> Handles => [.. Window.GetVisualDescendants().OfType<SelectionHandle>().OrderByDescending(handle => handle.Tip)];

        public Border? Bar => Window.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "SelectionBar");

        public string Selected => string.Join('|', Blocks.Select(block => block.SelectedText).Where(text => text.Length > 0));

        private static Type Raw(string name) => typeof(Avalonia.Input.Pointer).Assembly.GetType(name, true)!;

        // Touches are tested against what was last rendered, so a frame has to be drawn before they can hit anything new.
        public void Settle()
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        // Sends a touch the way a phone does, so hit testing, focus, capture and the scroll gesture all take part.
        public void Touch(string type, Avalonia.Point at)
        {
            object root = impl.GetType().GetProperty("InputRoot")!.GetValue(impl)!;
            object args = Activator.CreateInstance(Raw("Avalonia.Input.Raw.RawTouchEventArgs"), device, time += 16, root, Enum.Parse(Raw("Avalonia.Input.Raw.RawPointerEventType"), type), at, Avalonia.Input.RawInputModifiers.None, 1L)!;
            Delegate input = (Delegate)impl.GetType().GetProperty("Input")!.GetValue(impl)!;
            input.DynamicInvoke(args);
            Settle();
        }

        public void Tap(Avalonia.Point at)
        {
            Touch("TouchBegin", at);
            Touch("TouchEnd", at);
        }

        // The dispatcher only runs its timers while it is not busy with the test, so waiting has to give it up.
        public async Task Wait(int milliseconds)
        {
            await Task.Delay(milliseconds);
            Settle();
        }

        public async Task Hold(Avalonia.Point at)
        {
            Touch("TouchBegin", at);
            await Wait(650);
            Touch("TouchEnd", at);
        }

        public void Drag(Avalonia.Point from, Avalonia.Point to, bool release = true, bool begin = true)
        {
            if (begin)
            {
                Touch("TouchBegin", from);
            }

            for (int step = 1; step <= 8; step++)
            {
                Touch("TouchUpdate", from + ((to - from) * (step / 8.0)));
            }

            if (release)
            {
                Touch("TouchEnd", to);
            }
        }

        // Where a character is on the screen: the middle of its line, just inside its left edge.
        public Avalonia.Point At(int block, int character)
        {
            ScrollSafeTextBlock text = Blocks[block];
            Avalonia.Rect place = text.TextLayout.HitTestTextPosition(character);
            return Avalonia.VisualExtensions.TranslatePoint(text, new Avalonia.Point(place.X + 1, place.Center.Y), Window)!.Value;
        }

        public Avalonia.Point TipOf(SelectionHandle handle) => new(Avalonia.Controls.Canvas.GetLeft(handle) + handle.Tip, Avalonia.Controls.Canvas.GetTop(handle));

        // Takes a handle by the middle of its drop and moves it so that its tip ends up under a character.
        public void Pull(SelectionHandle handle, Avalonia.Point target, bool release = true)
        {
            Avalonia.Point tip = TipOf(handle);
            Avalonia.Point grab = tip + new Avalonia.Point(handle.Tip > handle.Width / 2 ? -11 : 11, 14);
            Drag(grab, grab + (new Avalonia.Point(target.X, target.Y + 9) - tip), release);
        }

        public void Press(string content)
        {
            Button button = Bar!.GetVisualDescendants().OfType<Button>().Single(item => Equals(item.Content, content));
            Tap(Avalonia.VisualExtensions.TranslatePoint(button, new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value);
        }

        public void Dispose() => Window.Close();
    }

    [Fact]
    public async Task AHeldWordGetsAHandleAtEachEndAndABarInPlaceOfTheContextMenu()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);

            await screen.Hold(screen.At(0, 7));

            Assert.Equal("bravo", screen.Selected.Trim());
            Assert.Equal(2, screen.Handles.Count);
            Assert.All(screen.Handles, handle => Assert.True(handle.IsVisible));
            Assert.Equal(["Copy", "Select All"], screen.Bar!.GetVisualDescendants().OfType<Button>().Select(button => (string)button.Content!));
            Assert.InRange(screen.TipOf(screen.Handles[0]).X, screen.At(0, 6).X - 6, screen.At(0, 6).X + 6);
            Assert.InRange(screen.TipOf(screen.Handles[1]).X, screen.At(0, 11).X - 6, screen.At(0, 12).X + 6);
            Assert.All(screen.Blocks, block => Assert.False(block.ContextFlyout?.IsOpen ?? false));
            return true;
        });
    }

    [Theory]
    [InlineData(6, "bravo")]
    [InlineData(8, "bravo")]
    [InlineData(10, "bravo")]
    [InlineData(12, "charlie")]
    public async Task HoldingAnyLetterOfAWordSelectsThatWordAndNothingBeforeIt(int character, string word)
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);

            await screen.Hold(screen.At(0, character));

            Assert.Equal(word, screen.Selected.Trim().TrimEnd('.'));
            return true;
        });
    }

    [Fact]
    public async Task TheHandlesDragTheEndsAcrossParagraphsListItemsAndCodeBothWays()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);
            await screen.Hold(screen.At(1, 7));

            screen.Pull(screen.Handles[1], screen.At(4, 4));

            Assert.Equal("echo foxtrot.|Golf hotel|India juliet|kilo", screen.Selected);

            screen.Pull(screen.Handles[0], screen.At(0, 6));

            Assert.Equal("bravo charlie.|Delta echo foxtrot.|Golf hotel|India juliet|kilo", screen.Selected);

            screen.Pull(screen.Handles[1], screen.At(1, 5));

            Assert.Equal("bravo charlie.|Delta", screen.Selected);
            Assert.True(screen.Bar!.IsVisible);
            return true;
        });
    }

    [Fact]
    public async Task AHandleStopsShortOfTheOtherEndRatherThanCrossingIt()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);
            await screen.Hold(screen.At(1, 7));

            screen.Pull(screen.Handles[1], screen.At(0, 2));

            Assert.Equal("e", screen.Selected);

            screen.Pull(screen.Handles[0], screen.At(4, 4));

            Assert.Equal("e", screen.Selected);
            Assert.Equal(2, screen.Handles.Count);
            return true;
        });
    }

    [Fact]
    public async Task TheHoldingFingerDraggedOnStretchesTheSelectionFromTheHeldWordInsteadOfScrolling()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message, below: 900);
            Avalonia.Point word = screen.At(1, 7);
            screen.Touch("TouchBegin", word);
            await screen.Wait(650);

            screen.Drag(word, screen.At(3, 4), release: false, begin: false);

            Assert.Equal("echo foxtrot.|Golf hotel|Indi", screen.Selected);
            Assert.False(screen.Bar!.IsVisible);

            screen.Drag(screen.At(3, 4), screen.At(0, 6), release: false, begin: false);

            Assert.Equal("bravo charlie.|Delta echo", screen.Selected);

            screen.Touch("TouchEnd", screen.At(0, 6));
            await screen.Wait(300);

            Assert.Equal(0, screen.Scroller.Offset.Y);
            Assert.Equal("bravo charlie.|Delta echo", screen.Selected);
            Assert.Equal(2, screen.Handles.Count);
            Assert.True(screen.Bar!.IsVisible);
            return true;
        });
    }

    [Fact]
    public async Task ScrollingKeepsTheSelectionAndMovesItsHandlesAndATapEndsIt()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message, below: 900);
            await screen.Hold(screen.At(1, 7));
            double before = screen.TipOf(screen.Handles[0]).Y;

            screen.Drag(screen.At(4, 2), screen.At(4, 2) - new Avalonia.Point(0, 60));

            Assert.True(screen.Scroller.Offset.Y > 20, $"The list scrolled to {screen.Scroller.Offset.Y}.");
            Assert.Equal("echo", screen.Selected.Trim());
            Assert.Equal(before - screen.Scroller.Offset.Y, screen.TipOf(screen.Handles[0]).Y, 1.0);

            screen.Scroller.Offset = new Avalonia.Vector(0, 500);
            screen.Settle();

            Assert.All(screen.Handles, handle => Assert.False(handle.IsVisible));
            Assert.False(screen.Bar!.IsVisible);

            screen.Scroller.Offset = default;
            screen.Settle();

            Assert.All(screen.Handles, handle => Assert.True(handle.IsVisible));

            screen.Tap(new Avalonia.Point(200, 500));

            Assert.Equal(string.Empty, screen.Selected);
            Assert.Empty(screen.Handles);
            Assert.Null(screen.Bar);
            return true;
        });
    }

    [Fact]
    public async Task ATapOnTextEndsTheSelectionAndAnotherHoldStartsOverThere()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);
            await screen.Hold(screen.At(0, 7));

            screen.Tap(screen.At(5, 2));

            Assert.Equal(string.Empty, screen.Selected);
            Assert.Empty(screen.Handles);

            await screen.Hold(screen.At(0, 7));
            await screen.Hold(screen.At(5, 7));

            Assert.Equal("papa", screen.Selected.Trim());
            Assert.Equal(2, screen.Handles.Count);
            return true;
        });
    }

    [Fact]
    public async Task SelectAllTakesTheWholeMessageAndCopyPutsItsLinesOnTheClipboard()
    {
        string? copied = await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);
            await screen.Hold(screen.At(0, 7));

            screen.Press("Select All");

            Assert.Equal(6, screen.Selected.Split('|').Length);
            Assert.Equal(2, screen.Handles.Count);

            screen.Press("Copy");

            Assert.Empty(screen.Handles);
            Assert.Equal(string.Empty, screen.Selected);
            return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(screen.Window.Clipboard!);
        });

        Assert.Equal("Alpha bravo charlie.\nDelta echo foxtrot.\nGolf hotel\nIndia juliet\nkilo lima\nmike november\nOscar papa quebec.", copied);
    }

    [Fact]
    public async Task AHandleTakenToTheBottomEdgeScrollsOnAndReachesTextThatWasOutOfSight()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(string.Join("\n\n", Enumerable.Range(1, 60).Select(number => $"Paragraph number {number} here.")));
            await screen.Hold(screen.At(0, 2));
            SelectionHandle end = screen.Handles[1];

            screen.Pull(end, new Avalonia.Point(200, 598), false);

            await screen.Wait(3000);

            Assert.True(screen.Scroller.Offset.Y > 200, $"The list scrolled to {screen.Scroller.Offset.Y}.");
            Assert.Contains("number 20 here", screen.Selected);
            Assert.False(screen.Bar!.IsVisible);

            screen.Touch("TouchEnd", new Avalonia.Point(200, 598));
            double settled = screen.Scroller.Offset.Y;
            await screen.Wait(150);

            Assert.Equal(settled, screen.Scroller.Offset.Y);
            Assert.True(screen.Bar!.IsVisible);
            Assert.InRange(Avalonia.Controls.Canvas.GetTop(screen.Bar), 0, 600);
            return true;
        });
    }

    [Fact]
    public async Task InTheChatOfAPhoneAHeldWordCanBeDraggedOutAndSurvivesScrollingUntilAnotherTabShows()
    {
        Mock<IServerConnection> connection = new();
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };
        string answer = string.Join("\n\n", Enumerable.Range(1, 40).Select(number => $"Paragraph number {number} here."));
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.Assistant, Text = answer }] });

        await fixture.Dispatch(async () =>
        {
            HomeViewModel model = new(connection.Object, ClientLayout.Tabs);
            await model.Start(workspace.Id);
            model.IsDrawerOpen = false;
            using Screen screen = new(new HomeView { DataContext = model });
            await screen.Wait(300);
            screen.Scroller.Offset = default;
            await screen.Wait(200);

            await screen.Hold(screen.At(1, 3));

            Assert.Equal("Paragraph", screen.Selected.Trim());
            Assert.Equal(2, screen.Handles.Count);

            screen.Pull(screen.Handles[1], screen.At(3, 16));

            Assert.Equal("Paragraph number 2 here.|Paragraph number 3 here.|Paragraph number", screen.Selected);
            Assert.True(screen.Bar!.IsVisible);

            double before = screen.Scroller.Offset.Y;
            screen.Drag(screen.At(10, 2), screen.At(10, 2) - new Avalonia.Point(0, 50));
            await screen.Wait(100);

            Assert.True(screen.Scroller.Offset.Y > before + 20, $"The chat scrolled from {before} to {screen.Scroller.Offset.Y}.");
            Assert.StartsWith("Paragraph number 2 here.", screen.Selected);

            model.Active!.ShowScriptCommand.Execute(null);
            await screen.Wait(100);

            Assert.Empty(screen.Handles);
            return true;
        });
    }

    [Fact]
    public async Task ASelectionInPlainTextStaysWhileTheTextGrowsAndEndsWhenShorterTextReplacesIt()
    {
        await fixture.Dispatch(async () =>
        {
            ScrollSafeTextBlock output = new() { Text = "first line of output", Margin = new Avalonia.Thickness(16, 80) };
            using Screen screen = new(output);
            Avalonia.Rect place = output.TextLayout.HitTestTextPosition(11);
            await screen.Hold(Avalonia.VisualExtensions.TranslatePoint(output, new Avalonia.Point(place.X + 1, place.Center.Y), screen.Window)!.Value);

            output.Text += "\nmore output";
            screen.Settle();

            Assert.Equal("of", output.SelectedText.Trim());
            Assert.Equal(2, screen.Handles.Count);

            output.Text = "new run";
            screen.Settle();

            Assert.Empty(screen.Handles);
            Assert.Null(screen.Bar);
            return true;
        });
    }

    [Fact]
    public async Task ASelectionEndsWhenItsMessageIsRenderedAgainOrHidden()
    {
        await fixture.Dispatch(async () =>
        {
            using Screen screen = new(message);
            await screen.Hold(screen.At(0, 7));

            screen.View.Source = message + "\n\nMore.";
            screen.Settle();

            Assert.Empty(screen.Handles);
            Assert.Null(screen.Bar);

            await screen.Hold(screen.At(0, 7));
            screen.Scroller.IsVisible = false;
            screen.Settle();

            Assert.Empty(screen.Handles);
            Assert.Null(screen.Bar);
            return true;
        });
    }
}
