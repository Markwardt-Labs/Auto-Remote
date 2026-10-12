namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A branch or tag badge on a commit of the history, with the actions it offers.
/// </summary>
internal sealed class GitRefViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitRefViewModel" /> class.
    /// </summary>
    /// <param name="reference">The branch or tag.</param>
    /// <param name="git">The git state the actions run through.</param>
    /// <param name="currentBranch">The checked out branch, or <see langword="null" /> when the head is detached.</param>
    /// <param name="remotes">The remotes whose copy of this branch is on the same commit, shown in the same label.</param>
    /// <param name="toggle">The command of the commit the label sits on, which tapping the label runs; its own actions are in its context menu.</param>
    public GitRefViewModel(GitRef reference, GitViewModel git, string? currentBranch, IReadOnlyList<string>? remotes = null, ICommand? toggle = null)
    {
        Reference = reference;
        ToggleCommand = toggle;
        RemoteLabel = string.Join(", ", remotes ?? []);
        this.currentBranch = currentBranch;
        string target = Target;
        CheckoutCommand = new AsyncCommand(_ => git.Checkout(target));
        DeleteCommand = new AsyncCommand(_ => IsTag ? git.DeleteTag(target) : git.DeleteBranch(target));
        SquashCurrentCommand = new AsyncCommand(_ => git.Squash(this.currentBranch!, target));
        SquashSelectedCommand = new AsyncCommand(_ => git.Squash(target, this.currentBranch!));
        RebaseCurrentCommand = new AsyncCommand(_ => git.Rebase(this.currentBranch!, target));
        RebaseSelectedCommand = new AsyncCommand(_ => git.Rebase(target, this.currentBranch!));
        MergeCurrentCommand = new AsyncCommand(_ => git.Merge(this.currentBranch!, target));
        MergeSelectedCommand = new AsyncCommand(_ => git.Merge(target, this.currentBranch!));
    }

    private readonly string? currentBranch;

    private IBrush? accent;

    /// <summary>
    /// Gets or sets the color of the branch line the label sits on, which outlines the label.
    /// </summary>
    public IBrush? Accent
    {
        get => accent;
        set => Set(ref accent, value);
    }

    /// <summary>
    /// Gets the branch or tag.
    /// </summary>
    public GitRef Reference { get; }

    /// <summary>
    /// Gets the names of the remotes whose copy of the branch is on the same commit, which share this label.
    /// </summary>
    public string RemoteLabel { get; }

    /// <summary>
    /// Gets a value indicating whether the label also stands for the branch on a remote.
    /// </summary>
    public bool HasRemote => RemoteLabel.Length > 0;

    /// <summary>
    /// Gets the name shown.
    /// </summary>
    public string Name => Reference.Name;

    /// <summary>
    /// Gets the name the actions use: the branch a remote branch is named after, so checking it out creates a local branch.
    /// </summary>
    public string Target => Reference.Kind == GitRefKind.Remote && Reference.Name.Contains('/') ? Reference.Name[(Reference.Name.IndexOf('/') + 1)..] : Reference.Name;

    /// <summary>
    /// Gets a value indicating whether this is a local branch.
    /// </summary>
    public bool IsBranch => Reference.Kind == GitRefKind.Branch;

    /// <summary>
    /// Gets a value indicating whether this is a tag.
    /// </summary>
    public bool IsTag => Reference.Kind == GitRefKind.Tag;

    /// <summary>
    /// Gets a value indicating whether this is the checked out branch.
    /// </summary>
    public bool IsCurrent => Reference.IsCurrent;

    /// <summary>
    /// Gets a value indicating whether the ref can be checked out, which the checked out branch cannot.
    /// </summary>
    public bool CanCheckout => !IsCurrent;

    /// <summary>
    /// Gets a value indicating whether the ref can be deleted, which the checked out branch cannot.
    /// </summary>
    public bool CanDelete => !IsCurrent;

    /// <summary>
    /// Gets a value indicating whether squash, rebase and merge are offered against the checked out branch.
    /// </summary>
    public bool HasRelative => !IsTag && !IsCurrent && currentBranch is not null && Target != currentBranch;

    /// <summary>
    /// Gets the label of squashing the checked out branch against this one.
    /// </summary>
    public string SquashCurrentHeader => $"Squash {Current} since {Selected}";

    /// <summary>
    /// Gets the label of squashing this branch against the checked out one.
    /// </summary>
    public string SquashSelectedHeader => $"Squash {Selected} since {Current}";

    /// <summary>
    /// Gets the label of rebasing the checked out branch onto this one.
    /// </summary>
    public string RebaseCurrentHeader => $"Rebase {Current} onto {Selected}";

    /// <summary>
    /// Gets the label of rebasing this branch onto the checked out one.
    /// </summary>
    public string RebaseSelectedHeader => $"Rebase {Selected} onto {Current}";

    /// <summary>
    /// Gets the label of merging the checked out branch into this one.
    /// </summary>
    public string MergeCurrentHeader => $"Merge {Current} into {Selected}";

    /// <summary>
    /// Gets the label of merging this branch into the checked out one.
    /// </summary>
    public string MergeSelectedHeader => $"Merge {Selected} into {Current}";

    /// <summary>
    /// Gets the command of the commit the label sits on that opens or closes it, or <see langword="null" />.
    /// </summary>
    public ICommand? ToggleCommand { get; }

    /// <summary>
    /// Gets the command that checks the ref out.
    /// </summary>
    public AsyncCommand CheckoutCommand { get; }

    /// <summary>
    /// Gets the command that deletes the branch or tag.
    /// </summary>
    public AsyncCommand DeleteCommand { get; }

    /// <summary>
    /// Gets the command that squashes the checked out branch against this one.
    /// </summary>
    public AsyncCommand SquashCurrentCommand { get; }

    /// <summary>
    /// Gets the command that squashes this branch against the checked out one.
    /// </summary>
    public AsyncCommand SquashSelectedCommand { get; }

    /// <summary>
    /// Gets the command that rebases the checked out branch onto this one.
    /// </summary>
    public AsyncCommand RebaseCurrentCommand { get; }

    /// <summary>
    /// Gets the command that rebases this branch onto the checked out one.
    /// </summary>
    public AsyncCommand RebaseSelectedCommand { get; }

    /// <summary>
    /// Gets the command that merges the checked out branch into this one.
    /// </summary>
    public AsyncCommand MergeCurrentCommand { get; }

    /// <summary>
    /// Gets the command that merges this branch into the checked out one.
    /// </summary>
    public AsyncCommand MergeSelectedCommand { get; }

    private string Current => Quote(currentBranch ?? string.Empty, " (current)");

    private string Selected => Quote(Target, string.Empty);

    private string Quote(string name, string suffix) => $"'{name.Replace("_", "__")}'{suffix}";
}
