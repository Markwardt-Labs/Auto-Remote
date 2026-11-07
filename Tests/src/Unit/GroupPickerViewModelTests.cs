namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GroupPickerViewModelTests
{
    private readonly WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };

    private readonly WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner" };

    private readonly Mock<IServerConnection> connection = new();

    public GroupPickerViewModelTests() => inner = inner with { ParentId = work.Id };

    [Fact]
    public void Fill_OffersTheTopLevelThenEveryGroupByItsPathAndChoosesTheTopLevel()
    {
        GroupPickerViewModel picker = new();

        picker.Fill([work, inner, new WorkspaceGroup { Id = Guid.NewGuid(), Name = "Play" }]);

        Assert.Equal(["Top level", "Work", "Work / Inner", "Play"], picker.Choices.Select(choice => choice.Name));
        Assert.Null(picker.GroupId);
    }

    [Fact]
    public void Fill_KeepsTheChosenGroupWhileItExistsAndFallsBackToTheTopLevelWhenItIsGone()
    {
        GroupPickerViewModel picker = new();
        picker.Fill([work, inner]);
        picker.Selected = picker.Choices.Single(choice => choice.Id == inner.Id);
        List<string?> changed = [];
        picker.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        picker.Fill([work, inner]);
        Assert.Equal(inner.Id, picker.GroupId);

        picker.Fill([work]);
        Assert.Null(picker.GroupId);
        Assert.Contains(nameof(GroupPickerViewModel.GroupId), changed);
    }

    [Fact]
    public void Selected_IgnoresNull()
    {
        GroupPickerViewModel picker = new();

        picker.Selected = null!;

        Assert.Equal("Top level", picker.Selected.Name);
    }

    [Fact]
    public async Task Load_ReadsTheGroupsFromTheServerAndLeavesOnlyTheTopLevelWhenItCannotBeRead()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [work], Workspaces = [] });
        GroupPickerViewModel picker = new();
        await picker.Load(connection.Object);

        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        GroupPickerViewModel unreadable = new();
        await unreadable.Load(connection.Object);

        Assert.Equal(["Top level", "Work"], picker.Choices.Select(choice => choice.Name));
        Assert.Equal(["Top level"], unreadable.Choices.Select(choice => choice.Name));
    }
}
