namespace Markwardt.AutoRemote.Tests.Unit;

[Collection("Headless")]
public sealed class MarkdownNavigationTests(HeadlessFixture fixture)
{
    private readonly string nested = "# Title\n\nintro\n\n## A\n\n### A1\n\nbody a1\n\n### A2\n\nbody a2\n\n## B\n\n#### Deep\n\nbody deep";

    private async Task<T> Render<T>(string source, Func<MarkdownView, ScrollViewer, T> act, double height = 800) => await fixture.Run(() =>
    {
        MarkdownView view = new() { Source = source, Collapsible = true };
        ScrollViewer scroller = new() { Content = view };
        Window window = new() { Width = 500, Height = height, Content = scroller };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        T result = act(view, scroller);
        window.Close();
        return result;
    });

    private string Flatten(TextBlock block) => block.Text ?? string.Concat(block.Inlines!.OfType<Run>().Select(run => run.Text));

    private List<string> Shown(MarkdownView view) => [.. view.GetVisualDescendants().OfType<ScrollSafeTextBlock>().Where(block => block.IsEffectivelyVisible).Select(Flatten)];

    private string Tall(int sections) => "# T\n\n" + string.Join("\n\n", Enumerable.Range(1, sections).Select(number => $"## S{number}\n\n" + string.Join("\n\n", Enumerable.Range(0, 8).Select(line => $"paragraph {line} of section {number}"))));

    [Fact]
    public async Task Outline_ListsEveryHeadingWithItsNestingDepthParentAndSectionNumberAndLeavesTheTitleOfTheDocumentUnnumbered()
    {
        IReadOnlyList<MarkdownHeading> outline = await Render<IReadOnlyList<MarkdownHeading>>(nested, (view, _) => view.Outline);

        Assert.Equal(["Title", "A", "A1", "A2", "B", "Deep"], outline.Select(heading => heading.Title));
        Assert.Equal(["", "1", "1.1", "1.2", "2", "2.1"], outline.Select(heading => heading.Number));
        Assert.Equal([0, 1, 2, 2, 1, 2], outline.Select(heading => heading.Depth));
        Assert.Equal([-1, 0, 1, 1, 0, 4], outline.Select(heading => heading.Parent));
        Assert.Equal([1, 2, 3, 3, 2, 4], outline.Select(heading => heading.Level));
        Assert.Equal(["Title", "1  A", "1.1  A1"], outline.Take(3).Select(heading => heading.Label));
    }

    [Fact]
    public async Task Outline_NumbersFromOneWhenTheDocumentHasSeveralHeadingsOfTheTopLevel()
    {
        IReadOnlyList<MarkdownHeading> outline = await Render<IReadOnlyList<MarkdownHeading>>("# One\n\n## Sub\n\n# Two\n\n## Sub\n\n### Deeper", (view, _) => view.Outline);

        Assert.Equal(["1", "1.1", "2", "2.1", "2.1.1"], outline.Select(heading => heading.Number));
    }

    [Fact]
    public async Task Outline_IsEmptyWhenTheViewIsNotCollapsibleAndTheSourceIsGone()
    {
        (int plain, int cleared) = await fixture.Run(() =>
        {
            MarkdownView view = new() { Source = "# A\n\n## B" };
            int plain = view.Outline.Count;
            view.Collapsible = true;
            view.Source = null;
            return (plain, view.Outline.Count);
        });

        Assert.Equal((0, 0), (plain, cleared));
    }

    [Fact]
    public async Task Numbered_ShowsTheSectionNumberBeforeEachHeadingAndHidesThemAgain()
    {
        (string[] off, string[] on, string[] hidden) = await Render<(string[], string[], string[])>(nested, (view, _) =>
        {
            string[] Numbers() => [.. view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible && block.Classes.Contains("muted") && block.Opacity == 0.7).Select(block => block.Text ?? string.Empty)];
            string[] off = Numbers();
            view.Numbered = true;
            string[] on = Numbers();
            view.Numbered = false;
            return (off, on, Numbers());
        });

        Assert.Empty(off);
        Assert.Equal(["1", "1.1", "1.2", "2", "2.1"], on);
        Assert.Empty(hidden);
    }

    [Fact]
    public async Task EachSectionBodyIsIndentedByLessAtEveryLevelAndHasAGuideLineAtItsLeftEdge()
    {
        (double[] paddings, int guides, int hairlines) = await Render<(double[], int, int)>("# A\n\n## B\n\n### C\n\n#### D\n\ntext", (view, _) =>
        {
            Border[] frames = [.. view.GetVisualDescendants().OfType<Border>().Where(border => border.BorderThickness == new Avalonia.Thickness(1, 0, 0, 0))];
            int hairlines = view.GetVisualDescendants().OfType<Border>().Count(border => border.BorderThickness == new Avalonia.Thickness(0, 0, 0, 1));
            return ([.. frames.Select(border => border.Padding.Left)], frames.Count(border => border.BorderBrush is not null), hairlines);
        });

        Assert.Equal([16d, 14d, 12d, 10d], paddings);
        Assert.Equal(4, guides);
        Assert.Equal(2, hairlines);
    }

    [Fact]
    public async Task HeadingsGetDimmerAndLighterWithEachLevel()
    {
        (double[] fades, Avalonia.Media.FontWeight[] weights) = await Render<(double[], Avalonia.Media.FontWeight[])>("# A\n\n## B\n\n### C\n\n#### D\n\n##### E\n\n###### F", (view, _) =>
        {
            ScrollSafeTextBlock[] headings = [.. view.GetVisualDescendants().OfType<ScrollSafeTextBlock>()];
            return ([.. headings.Select(block => block.Opacity)], [.. headings.Select(block => block.FontWeight)]);
        });

        Assert.Equal([1d, 1d, 0.95, 0.9, 0.85, 0.8], fades);
        Assert.Equal([Avalonia.Media.FontWeight.Bold, Avalonia.Media.FontWeight.SemiBold, Avalonia.Media.FontWeight.SemiBold, Avalonia.Media.FontWeight.Medium, Avalonia.Media.FontWeight.Medium, Avalonia.Media.FontWeight.Normal], weights);
    }

    [Fact]
    public async Task HeadingsOfAChatMessageKeepTheirPlainLookBecauseOnlyFilePreviewsAreCollapsible()
    {
        (int guides, double fade) = await fixture.Run(() =>
        {
            MarkdownView view = new() { Source = "## B\n\ntext" };
            Window window = new() { Width = 400, Height = 300, Content = view };
            window.Show();
            int guides = view.GetVisualDescendants().OfType<Border>().Count(border => border.BorderThickness == new Avalonia.Thickness(1, 0, 0, 0));
            double fade = view.GetVisualDescendants().OfType<ScrollSafeTextBlock>().First().Opacity;
            window.Close();
            return (guides, fade);
        });

        Assert.Equal((0, 1d), (guides, fade));
    }

    [Theory]
    [InlineData(0, new[] { "Title", "intro", "A", "A1", "body a1", "A2", "body a2", "B", "Deep", "body deep" })]
    [InlineData(1, new[] { "Title" })]
    [InlineData(2, new[] { "Title", "intro", "A", "B" })]
    [InlineData(3, new[] { "Title", "intro", "A", "A1", "A2", "B", "Deep" })]
    public async Task FoldToLevel_ShowsTheHeadingsDownToTheLevelAndHidesWhatIsUnderThem(int level, string[] expected)
    {
        List<string> shown = await Render<List<string>>(nested, (view, _) =>
        {
            view.FoldToLevel(level);
            return Shown(view);
        });

        Assert.Equal(expected, shown);
    }

    [Fact]
    public async Task ScrollTo_OpensTheSectionsAHeadingIsNestedUnderAndBringsItIntoView()
    {
        (List<string> shown, bool all) = await Render<(List<string>, bool)>(nested, (view, _) =>
        {
            view.AllCollapsed = true;
            view.ScrollTo(5);
            return (Shown(view), view.AllCollapsed);
        });

        Assert.Equal(["Title", "intro", "A", "B", "Deep", "body deep"], shown);
        Assert.False(all);
    }

    [Fact]
    public async Task Trail_NamesTheHeadingsTheTopOfTheViewIsUnderAndIsEmptyAtTheVeryTop()
    {
        (string[] top, string[] below, string[] inside, double offset) = await Render<(string[], string[], string[], double)>(Tall(6), (view, scroller) =>
        {
            view.UpdateTrail(0);
            string[] top = [.. view.Trail.Select(heading => heading.Title)];
            view.ScrollTo(3);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            double offset = scroller.Offset.Y;
            view.UpdateTrail(offset);
            string[] below = [.. view.Trail.Select(heading => heading.Title)];
            view.UpdateTrail(offset + 40);
            return (top, below, [.. view.Trail.Select(heading => heading.Title)], offset);
        }, 400);

        Assert.True(offset > 0);
        Assert.Empty(top);
        Assert.Equal(["T", "S2"], below);
        Assert.Equal(["T", "S3"], inside);
    }

    [Fact]
    public async Task Trail_SkipsHeadingsThatAreFoldedAway()
    {
        string[] trail = await Render<string[]>(Tall(6), (view, scroller) =>
        {
            view.FoldToLevel(2);
            view.UpdateTrail(5);
            return [.. view.Trail.Select(heading => heading.Title)];
        }, 400);

        Assert.Equal(["T"], trail);
    }
}
