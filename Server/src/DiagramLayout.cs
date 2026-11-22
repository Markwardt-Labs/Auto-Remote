namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Lays out flowcharts and state diagrams so they read from the entry point onward. Mermaider's layout removes cycles in an arbitrary order, which drew loops (a retry, a return to idle) as the main flow with the entry state at the bottom; this lays the graph out without its back edges and then draws those backward.
/// </summary>
internal interface IDiagramLayout : Mermaider.Layout.IGraphLayoutProvider
{
}

/// <inheritdoc cref="IDiagramLayout" />
/// <param name="inner">The layout provider that does the actual layout.</param>
internal sealed class DiagramLayout(Mermaider.Layout.IGraphLayoutProvider inner) : IDiagramLayout
{
    /// <inheritdoc />
    public Mermaider.Models.PositionedClassDiagram LayoutClass(Mermaider.Models.ClassDiagram diagram) => inner.LayoutClass(diagram);

    /// <inheritdoc />
    public Mermaider.Models.PositionedErDiagram LayoutEr(Mermaider.Models.ErDiagram diagram) => inner.LayoutEr(diagram);

    /// <inheritdoc />
    public Mermaider.Models.PositionedGraph LayoutFlowchart(Mermaider.Models.MermaidGraph graph, Mermaider.Models.RenderOptions? options = null, Mermaider.Models.StrictStylingOptions? strict = null)
    {
        HashSet<int> back = BackEdges(graph);

        if (back.Count == 0)
        {
            return inner.LayoutFlowchart(graph, options, strict);
        }

        List<Mermaider.Models.MermaidEdge> edges = [.. graph.Edges];

        foreach (int index in back)
        {
            Mermaider.Models.MermaidEdge edge = edges[index];
            edges[index] = edge with { Source = edge.Target, Target = edge.Source, HasArrowStart = edge.HasArrowEnd, HasArrowEnd = edge.HasArrowStart };
        }

        Mermaider.Models.PositionedGraph laidOut = inner.LayoutFlowchart(graph with { Edges = edges }, options, strict);
        List<Mermaider.Models.PositionedEdge> drawn = [.. laidOut.Edges];
        bool sameOrder = drawn.Count == edges.Count && Enumerable.Range(0, edges.Count).All(index => drawn[index].Source == edges[index].Source && drawn[index].Target == edges[index].Target);

        foreach (int index in back)
        {
            int at = sameOrder ? index : drawn.FindIndex(edge => edge.Source == edges[index].Source && edge.Target == edges[index].Target && edge.Label == edges[index].Label);

            if (at >= 0)
            {
                Mermaider.Models.PositionedEdge edge = drawn[at];
                drawn[at] = edge with { Source = edge.Target, Target = edge.Source, HasArrowStart = edge.HasArrowEnd, HasArrowEnd = edge.HasArrowStart, Points = [.. edge.Points.Reverse()] };
            }
        }

        return laidOut with { Edges = drawn };
    }

    private HashSet<int> BackEdges(Mermaider.Models.MermaidGraph graph)
    {
        Dictionary<string, string> representative = [];

        string Represent(Mermaider.Models.MermaidSubgraph subgraph)
        {
            representative[subgraph.Id] = subgraph.NodeIds.FirstOrDefault() ?? subgraph.Children.Select(Represent).FirstOrDefault() ?? subgraph.Id;
            return representative[subgraph.Id];
        }

        foreach (Mermaider.Models.MermaidSubgraph subgraph in graph.Subgraphs)
        {
            Represent(subgraph);
        }

        string Map(string id) => representative.TryGetValue(id, out string? node) ? node : id;

        List<(string Source, string Target)> mapped = [.. graph.Edges.Select(edge => (Map(edge.Source), Map(edge.Target)))];
        Dictionary<string, List<(string Target, int Index)>> next = [];

        for (int index = 0; index < mapped.Count; index++)
        {
            if (mapped[index].Source != mapped[index].Target)
            {
                if (!next.TryGetValue(mapped[index].Source, out List<(string Target, int Index)>? list))
                {
                    list = [];
                    next[mapped[index].Source] = list;
                }

                list.Add((mapped[index].Target, index));
            }
        }

        HashSet<string> hasIncoming = [.. mapped.Where(edge => edge.Source != edge.Target).Select(edge => edge.Target)];
        List<string> order = [.. mapped.SelectMany(edge => new[] { edge.Source, edge.Target }).Distinct(), .. graph.NodeOrder];
        List<string> roots = [.. order.Where(id => !hasIncoming.Contains(id)), .. order.Where(hasIncoming.Contains)];
        Dictionary<string, int> state = [];
        HashSet<int> back = [];

        void Visit(string id)
        {
            state[id] = 1;

            if (next.TryGetValue(id, out List<(string Target, int Index)>? edges))
            {
                foreach ((string target, int index) in edges)
                {
                    int seen = state.GetValueOrDefault(target);

                    if (seen == 1)
                    {
                        back.Add(index);
                    }
                    else if (seen == 0)
                    {
                        Visit(target);
                    }
                }
            }

            state[id] = 2;
        }

        foreach (string id in roots.Where(id => !state.ContainsKey(id)))
        {
            Visit(id);
        }

        return back;
    }
}
