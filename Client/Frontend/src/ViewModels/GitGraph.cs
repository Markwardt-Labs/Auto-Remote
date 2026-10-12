namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One line of a history row's graph, from a lane at a height of the row to a lane at another height, with heights from the top of the row (0) to its bottom (1).
/// </summary>
/// <param name="FromLane">The lane the line starts in.</param>
/// <param name="FromY">The height the line starts at.</param>
/// <param name="ToLane">The lane the line ends in.</param>
/// <param name="ToY">The height the line ends at.</param>
/// <param name="Color">The index of the line's color.</param>
internal sealed record GitGraphLine(int FromLane, double FromY, int ToLane, double ToY, int Color);

/// <summary>
/// The graph drawn beside one commit of the history: its node and the lines of the branches passing through its row.
/// </summary>
internal sealed record GitGraphRow
{
    /// <summary>
    /// Gets the lane of the commit's node.
    /// </summary>
    public required int Lane { get; init; }

    /// <summary>
    /// Gets the index of the node's color.
    /// </summary>
    public required int Color { get; init; }

    /// <summary>
    /// Gets the lines inside the row.
    /// </summary>
    public required IReadOnlyList<GitGraphLine> Lines { get; init; }

    /// <summary>
    /// Gets the lanes, with their color, that carry on below the row.
    /// </summary>
    public required IReadOnlyList<(int Lane, int Color)> Continuing { get; init; }
}

/// <summary>
/// Lays out the branch graph of a repository's history, newest commit first, the way git graph viewers do: each branch line keeps a lane while it is open.
/// </summary>
internal sealed class GitGraph
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitGraph" /> class.
    /// </summary>
    /// <param name="commits">The commits, newest first, children before their parents.</param>
    public GitGraph(IReadOnlyList<GitCommit> commits)
    {
        List<(string Hash, int Color)?> lanes = [];
        int nextColor = 0;
        int widest = 0;

        foreach (GitCommit commit in commits)
        {
            List<(string Hash, int Color)?> before = [.. lanes];
            List<int> merging = [.. Enumerable.Range(0, before.Count).Where(index => before[index]?.Hash == commit.Hash)];
            int lane = merging.Count > 0 ? merging[0] : Free(lanes);
            int color = merging.Count > 0 ? before[lane]!.Value.Color : nextColor++ % colorCount;

            List<GitGraphLine> lines = [];

            for (int index = 0; index < before.Count; index++)
            {
                if (before[index] is not { } open)
                {
                    continue;
                }

                lines.Add(merging.Contains(index) ? new GitGraphLine(index, 0, lane, 0.5, open.Color) : new GitGraphLine(index, 0, index, 1, open.Color));
            }

            foreach (int index in merging)
            {
                lanes[index] = null;
            }

            for (int number = 0; number < commit.Parents.Count; number++)
            {
                string parent = commit.Parents[number];
                int existing = lanes.FindIndex(item => item?.Hash == parent);

                if (existing >= 0)
                {
                    lines.Add(new GitGraphLine(lane, 0.5, existing, 1, lanes[existing]!.Value.Color));
                }
                else if (number == 0)
                {
                    Put(lanes, lane, (parent, color));
                    lines.Add(new GitGraphLine(lane, 0.5, lane, 1, color));
                }
                else
                {
                    int target = Free(lanes);
                    int parentColor = nextColor++ % colorCount;
                    Put(lanes, target, (parent, parentColor));
                    lines.Add(new GitGraphLine(lane, 0.5, target, 1, parentColor));
                }
            }

            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
            }

            widest = Math.Max(widest, Math.Max(before.Count, Math.Max(lanes.Count, lane + 1)));
            rows.Add(new GitGraphRow
            {
                Lane = lane,
                Color = color,
                Lines = lines,
                Continuing = [.. Enumerable.Range(0, lanes.Count).Where(index => lanes[index] is not null).Select(index => (index, lanes[index]!.Value.Color))],
            });
        }

        Lanes = widest;
    }

    private readonly int colorCount = 12;

    private readonly List<GitGraphRow> rows = [];

    /// <summary>
    /// Gets the rows, one per commit in order.
    /// </summary>
    public IReadOnlyList<GitGraphRow> Rows => rows;

    /// <summary>
    /// Gets how many lanes the widest row needs.
    /// </summary>
    public int Lanes { get; }

    private int Free(List<(string Hash, int Color)?> lanes)
    {
        int free = lanes.FindIndex(item => item is null);
        return free >= 0 ? free : lanes.Count;
    }

    private void Put(List<(string Hash, int Color)?> lanes, int lane, (string Hash, int Color) value)
    {
        while (lanes.Count <= lane)
        {
            lanes.Add(null);
        }

        lanes[lane] = value;
    }
}
