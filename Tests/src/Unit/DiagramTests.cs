namespace Markwardt.AutoRemote.Tests.Unit;

[Collection("Headless")]
public sealed class DiagramTests(HeadlessFixture fixture)
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"60\"><rect width=\"120\" height=\"60\" fill=\"#2D2D2D\"/><text x=\"10\" y=\"35\" fill=\"#CCCCCC\">Hi</text></svg>";

    private readonly string diagram = "```mermaid\ngraph TD\n  A --> B\n```";

    private DiagramPicture Picture() => new() { Scale = 2, Image = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(20, 10), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul), Width = 120, Height = 60 };

    [Fact]
    public async Task Draw_AsksTheServerOnceAndTurnsTheSvgIntoASharpPictureOfTheSizeTheSvgGives()
    {
        connection.Setup(x => x.RenderDiagram("graph TD\n  A --> B", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await Task.Delay(50);
            return svg;
        });
        DiagramSource source = new(connection.Object);

        (DiagramPicture[] pictures, bool found, bool sameFound) = await fixture.RunAsync(async () =>
        {
            Task<DiagramPicture>[] waiting = [source.Draw("graph TD\n  A --> B", CancellationToken.None), source.Draw(" graph TD\n  A --> B ", CancellationToken.None)];
            DiagramPicture[] done = await Task.WhenAll(waiting);
            return (done, source.Find("graph TD\n  A --> B") is not null, ReferenceEquals(source.Find("graph TD\n  A --> B"), done[0]));
        });

        Assert.Same(pictures[0], pictures[1]);
        Assert.Equal((120d, 60d), (pictures[0].Width, pictures[0].Height));
        Assert.Equal(3d, pictures[0].Scale);
        Assert.True(found);
        Assert.True(sameFound);
        connection.Verify(x => x.RenderDiagram(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Draw_KeepsAHugeDiagramWithinWhatAScreenCanTakeAsATexture()
    {
        connection.Setup(x => x.RenderDiagram(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20000\" height=\"400\"><rect width=\"20000\" height=\"400\" fill=\"#2D2D2D\"/></svg>");
        DiagramSource source = new(connection.Object);

        DiagramPicture picture = await fixture.RunAsync(() => source.Draw("graph LR\n  A --> B", CancellationToken.None));

        Assert.Equal((20000d, 400d), (picture.Width, picture.Height));
        Assert.InRange(picture.Width * picture.Scale, 1, 4096);
    }

    [Fact]
    public async Task Draw_ReportsWhyTheServerCouldNotDrawItAndSvgThatIsNone()
    {
        connection.Setup(x => x.RenderDiagram("bad", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The diagram cannot be drawn: bad"));
        connection.Setup(x => x.RenderDiagram("odd", It.IsAny<CancellationToken>())).ReturnsAsync("this is not svg");
        DiagramSource source = new(connection.Object);

        (RemoteException? refused, InvalidDataException? odd) = await fixture.RunAsync(async () =>
        {
            RemoteException? refused = null;
            InvalidDataException? odd = null;

            try
            {
                await source.Draw("bad", CancellationToken.None);
            }
            catch (RemoteException exception)
            {
                refused = exception;
            }

            try
            {
                await source.Draw("odd", CancellationToken.None);
            }
            catch (InvalidDataException exception)
            {
                odd = exception;
            }

            return (refused, odd);
        });

        Assert.Equal("The diagram cannot be drawn: bad", refused?.Message);
        Assert.NotNull(odd);
        Assert.Null(source.Find("bad"));
    }

    private (bool Image, string[] Texts, string Plain) Show(string markdown, IDiagramSource? diagrams, Func<bool>? waitFor = null, bool click = false) => fixture.Run(() =>
    {
        MarkdownView view = new() { Source = markdown, Diagrams = diagrams };
        Window window = new() { Width = 500, Height = 600, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        if (click)
        {
            view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Show Diagram")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }

        for (int attempt = 0; attempt < 100 && waitFor is not null && !waitFor(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        bool image = view.GetVisualDescendants().OfType<Image>().Any();
        string[] texts = [.. view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text)))];
        string plain = view.PlainText;
        window.Close();
        return (image, texts, plain);
    }).GetAwaiter().GetResult();

    [Fact]
    public void MarkdownView_ShowsAMermaidBlockAsItsPictureWhenThereAreDiagramsAndLeavesItsTextOutOfWhatFindSearches()
    {
        Mock<IDiagramSource> diagrams = new();
        diagrams.Setup(x => x.Find("graph TD\n  A --> B")).Returns(() => Picture());

        (bool image, string[] texts, string plain) = Show($"before\n\n{diagram}\n\nafter", diagrams.Object);

        Assert.True(image);
        Assert.DoesNotContain(texts, text => text.Contains("graph TD", StringComparison.Ordinal));
        Assert.Equal("before\nafter\n", plain);
    }

    [Fact]
    public void MarkdownView_ShowsAMermaidBlockAsCodeWhereThereAreNoDiagramsAsTheConversationDoes()
    {
        (bool image, string[] texts, string plain) = Show(diagram, null);

        Assert.False(image);
        Assert.Contains(texts, text => text.Contains("graph TD", StringComparison.Ordinal));
        Assert.Contains("graph TD", plain);
    }

    [Fact]
    public void MarkdownView_OffersAButtonForAMermaidBlockAndOnlyAsksTheServerOnceItIsClicked()
    {
        TaskCompletionSource<DiagramPicture> drawing = new();
        Mock<IDiagramSource> diagrams = new();
        diagrams.Setup(x => x.Draw("graph TD\n  A --> B", It.IsAny<CancellationToken>())).Returns(drawing.Task);

        (bool offeredImage, string[] offeredTexts, _) = Show(diagram, diagrams.Object);
        diagrams.Verify(x => x.Draw(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (bool waitingImage, string[] waitingTexts, _) = Show(diagram, diagrams.Object, () => false, true);

        Assert.False(offeredImage);
        Assert.DoesNotContain("Drawing the diagram...", offeredTexts);
        Assert.False(waitingImage);
        Assert.Contains("Drawing the diagram...", waitingTexts);
    }

    [Fact]
    public void MarkdownView_ExplainsWhyADiagramCannotBeDrawnAndShowsItsText()
    {
        Mock<IDiagramSource> diagrams = new();
        diagrams.Setup(x => x.Draw("graph TD\n  A --> B", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The diagram cannot be drawn: oops"));

        (bool image, string[] texts, _) = Show(diagram, diagrams.Object, () => false, true);

        Assert.False(image);
        Assert.Contains(texts, text => text == "The diagram cannot be drawn: oops");
        Assert.Contains(texts, text => text.Contains("graph TD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkdownView_ZoomsADrawnDiagramWithItsButtonsWithinTheLimitsAndResetsIt()
    {
        Mock<IDiagramSource> diagrams = new();
        diagrams.Setup(x => x.Find("graph TD\n  A --> B")).Returns(() => Picture());

        (double[] widths, string[] labels) = await fixture.Run(() =>
        {
            MarkdownView view = new() { Source = diagram, Diagrams = diagrams.Object };
            Window window = new() { Width = 500, Height = 600, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Button Find(string tip) => view.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), tip));
            void Press(string tip) => Find(tip).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            double Width() => view.GetVisualDescendants().OfType<Image>().Single().Width;

            List<double> seen = [Width()];
            Press("Zoom In");
            seen.Add(Width());
            Press("Reset Zoom");
            seen.Add(Width());
            Press("Zoom Out");
            seen.Add(Width());

            for (int press = 0; press < 20; press++)
            {
                Press("Zoom Out");
            }

            seen.Add(Width());
            string small = Find("Reset Zoom").Content!.ToString()!;

            for (int press = 0; press < 40; press++)
            {
                Press("Zoom In");
            }

            seen.Add(Width());
            string large = Find("Reset Zoom").Content!.ToString()!;
            window.Close();
            return (seen.ToArray(), new[] { small, large });
        });

        Assert.Equal([120, 150, 120, 96, 30, 480], widths);
        Assert.Equal(["25%", "400%"], labels);
    }
}
