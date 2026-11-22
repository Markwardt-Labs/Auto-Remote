namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class DiagramLayoutTests
{
    private readonly Mock<Mermaider.Layout.IGraphLayoutProvider> inner = new();

    private T Blank<T>() => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    private Mermaider.Models.MermaidGraph Graph(params (string Source, string Target)[] edges) => new()
    {
        Direction = default,
        Nodes = new Dictionary<string, Mermaider.Models.MermaidNode>(),
        NodeOrder = [.. edges.SelectMany(edge => new[] { edge.Source, edge.Target }).Distinct()],
        Edges = [.. edges.Select(edge => new Mermaider.Models.MermaidEdge(edge.Source, edge.Target, "go", default, false, true, 1))],
        Subgraphs = [],
    };

    private Mermaider.Models.PositionedGraph Positioned(Mermaider.Models.MermaidGraph graph) => new()
    {
        Width = 100,
        Height = 100,
        Nodes = [],
        Groups = [],
        Notes = [],
        Edges = [.. graph.Edges.Select(edge => new Mermaider.Models.PositionedEdge
        {
            Source = edge.Source,
            Target = edge.Target,
            Label = edge.Label,
            Style = default,
            HasArrowEnd = edge.HasArrowEnd,
            HasArrowStart = edge.HasArrowStart,
            Points = [new Mermaider.Models.Point(1, 1), new Mermaider.Models.Point(2, 2), new Mermaider.Models.Point(3, 3)],
        })],
    };

    [Fact]
    public void LayoutFlowchart_LaysTheGraphOutWithoutItsBackEdgesAndDrawsThemBackwardAfterwards()
    {
        Mermaider.Models.MermaidGraph? laidOut = null;
        inner.Setup(x => x.LayoutFlowchart(It.IsAny<Mermaider.Models.MermaidGraph>(), It.IsAny<Mermaider.Models.RenderOptions?>(), It.IsAny<Mermaider.Models.StrictStylingOptions?>()))
            .Returns((Mermaider.Models.MermaidGraph graph, Mermaider.Models.RenderOptions? _, Mermaider.Models.StrictStylingOptions? _) =>
            {
                laidOut = graph;
                return Positioned(graph);
            });
        DiagramLayout layout = new(inner.Object);

        Mermaider.Models.PositionedGraph result = layout.LayoutFlowchart(Graph(("Start", "Work"), ("Work", "Check"), ("Check", "Work"), ("Check", "End")));

        Assert.NotNull(laidOut);
        Assert.Contains(laidOut.Edges, edge => edge.Source == "Work" && edge.Target == "Check");
        Assert.DoesNotContain(laidOut.Edges, edge => edge.Source == "Check" && edge.Target == "Work");
        Mermaider.Models.PositionedEdge back = Assert.Single(result.Edges, edge => edge.Source == "Check" && edge.Target == "Work");
        Assert.True(back.HasArrowEnd);
        Assert.False(back.HasArrowStart);
        Assert.Equal([3d, 2d, 1d], back.Points.Select(point => point.X));
        Assert.Equal(4, result.Edges.Count);
    }

    [Fact]
    public void LayoutFlowchart_StartsFromTheFirstEdgeSoALaterEdgeBackToItIsTheBackEdge()
    {
        Mermaider.Models.MermaidGraph? laidOut = null;
        inner.Setup(x => x.LayoutFlowchart(It.IsAny<Mermaider.Models.MermaidGraph>(), It.IsAny<Mermaider.Models.RenderOptions?>(), It.IsAny<Mermaider.Models.StrictStylingOptions?>()))
            .Returns((Mermaider.Models.MermaidGraph graph, Mermaider.Models.RenderOptions? _, Mermaider.Models.StrictStylingOptions? _) =>
            {
                laidOut = graph;
                return Positioned(graph);
            });
        DiagramLayout layout = new(inner.Object);

        layout.LayoutFlowchart(Graph(("Idle", "Busy"), ("Busy", "Failed"), ("Failed", "Idle"), ("Busy", "Idle")));

        Assert.NotNull(laidOut);
        Assert.Equal([("Idle", "Busy"), ("Busy", "Failed"), ("Idle", "Failed"), ("Idle", "Busy")], laidOut.Edges.Select(edge => (edge.Source, edge.Target)));
    }

    [Fact]
    public void LayoutFlowchart_PassesAGraphWithoutCyclesOnUnchanged()
    {
        Mermaider.Models.MermaidGraph graph = Graph(("A", "B"), ("B", "C"), ("A", "C"), ("C", "C"));
        Mermaider.Models.PositionedGraph positioned = Positioned(graph);
        inner.Setup(x => x.LayoutFlowchart(graph, null, null)).Returns(positioned);
        DiagramLayout layout = new(inner.Object);

        Assert.Same(positioned, layout.LayoutFlowchart(graph));
    }

    [Fact]
    public void LayoutFlowchart_FollowsAnEdgeBetweenSubgraphsToTheirNodes()
    {
        Mermaider.Models.MermaidGraph graph = Graph(("Ui", "Api"), ("Api", "Ui")) with
        {
            Subgraphs = [new Mermaider.Models.MermaidSubgraph { Id = "Front", Label = "Front", NodeIds = ["Ui"], Children = [] }, new Mermaider.Models.MermaidSubgraph { Id = "Back", Label = "Back", NodeIds = ["Api"], Children = [] }],
            Edges = [new Mermaider.Models.MermaidEdge("Ui", "Api", "go", default, false, true, 1), new Mermaider.Models.MermaidEdge("Back", "Front", "back", default, false, true, 1)],
        };
        Mermaider.Models.MermaidGraph? laidOut = null;
        inner.Setup(x => x.LayoutFlowchart(It.IsAny<Mermaider.Models.MermaidGraph>(), It.IsAny<Mermaider.Models.RenderOptions?>(), It.IsAny<Mermaider.Models.StrictStylingOptions?>()))
            .Returns((Mermaider.Models.MermaidGraph given, Mermaider.Models.RenderOptions? _, Mermaider.Models.StrictStylingOptions? _) =>
            {
                laidOut = given;
                return Positioned(given);
            });
        DiagramLayout layout = new(inner.Object);

        layout.LayoutFlowchart(graph);

        Assert.NotNull(laidOut);
        Assert.Equal([("Ui", "Api"), ("Front", "Back")], laidOut.Edges.Select(edge => (edge.Source, edge.Target)));
    }

    [Fact]
    public void LayoutClassAndLayoutEr_AreLeftToTheInnerProvider()
    {
        Mermaider.Models.ClassDiagram classes = Blank<Mermaider.Models.ClassDiagram>();
        Mermaider.Models.ErDiagram entities = Blank<Mermaider.Models.ErDiagram>();
        Mermaider.Models.PositionedClassDiagram positionedClasses = Blank<Mermaider.Models.PositionedClassDiagram>();
        Mermaider.Models.PositionedErDiagram positionedEntities = Blank<Mermaider.Models.PositionedErDiagram>();
        inner.Setup(x => x.LayoutClass(classes)).Returns(positionedClasses);
        inner.Setup(x => x.LayoutEr(entities)).Returns(positionedEntities);
        DiagramLayout layout = new(inner.Object);

        Assert.Same(positionedClasses, layout.LayoutClass(classes));
        Assert.Same(positionedEntities, layout.LayoutEr(entities));
    }
}
