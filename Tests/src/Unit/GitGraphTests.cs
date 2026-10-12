namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GitGraphTests
{
    private GitCommit Commit(string hash, params string[] parents) => new() { Hash = hash, Subject = hash, Author = "Ann", Date = DateTimeOffset.UnixEpoch, Parents = parents };

    [Fact]
    public void LinearHistory_StaysInOneLane()
    {
        GitGraph graph = new([Commit("c", "b"), Commit("b", "a"), Commit("a")]);

        Assert.Equal(1, graph.Lanes);
        Assert.All(graph.Rows, row => Assert.Equal(0, row.Lane));
        Assert.Equal([new GitGraphLine(0, 0.5, 0, 1, graph.Rows[0].Color)], graph.Rows[0].Lines);
        Assert.Equal([new GitGraphLine(0, 0, 0, 0.5, graph.Rows[0].Color), new GitGraphLine(0, 0.5, 0, 1, graph.Rows[0].Color)], graph.Rows[1].Lines);
        Assert.Empty(graph.Rows[2].Continuing);
    }

    [Fact]
    public void Merge_OpensALaneForTheSecondParentAndClosesItWhereTheBranchesMeet()
    {
        GitGraph graph = new([Commit("m", "a", "b"), Commit("a", "base"), Commit("b", "base"), Commit("base")]);

        Assert.Equal(2, graph.Lanes);
        Assert.Equal([0, 0, 1, 0], graph.Rows.Select(row => row.Lane));
        Assert.Contains(graph.Rows[0].Lines, line => line.FromLane == 0 && line.ToLane == 1 && line.FromY == 0.5 && line.ToY == 1);
        Assert.Contains(graph.Rows[1].Lines, line => line.FromLane == 1 && line.ToLane == 1 && line.FromY == 0 && line.ToY == 1);
        Assert.Contains(graph.Rows[2].Lines, line => line.FromLane == 1 && line.ToLane == 0 && line.FromY == 0.5 && line.ToY == 1);
        Assert.Contains(graph.Rows[3].Lines, line => line.FromLane == 0 && line.ToLane == 0 && line.ToY == 0.5);
        Assert.Empty(graph.Rows[3].Continuing);
    }

    [Fact]
    public void ParallelBranchTips_GetTheirOwnLanesAndColors()
    {
        GitGraph graph = new([Commit("x", "base"), Commit("y", "base"), Commit("base")]);

        Assert.Equal([0, 1, 0], graph.Rows.Select(row => row.Lane));
        Assert.NotEqual(graph.Rows[0].Color, graph.Rows[1].Color);
        Assert.Equal(graph.Rows[0].Color, graph.Rows[2].Color);
        Assert.Equal(2, graph.Lanes);
    }

    [Fact]
    public void CommitsWhoseParentsAreNotLoaded_KeepTheirLaneOpenToTheBottom()
    {
        GitGraph graph = new([Commit("b", "a")]);

        Assert.Equal([(0, graph.Rows[0].Color)], graph.Rows[0].Continuing);
    }
}
