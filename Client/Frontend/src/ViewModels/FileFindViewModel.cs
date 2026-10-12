namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Searches the text of the file being previewed and steps through the matches.
/// </summary>
internal sealed class FileFindViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileFindViewModel" /> class.
    /// </summary>
    public FileFindViewModel()
    {
        NextCommand = new AsyncCommand(_ => Step(1), () => matches.Starts.Count > 0);
        PreviousCommand = new AsyncCommand(_ => Step(-1), () => matches.Starts.Count > 0);
        CloseCommand = new AsyncCommand(_ =>
        {
            IsOpen = false;
            return Task.CompletedTask;
        });
    }

    private string source = string.Empty;

    private string query = string.Empty;

    private bool isOpen;

    private bool matchCase;

    private bool wholeWord;

    private TextMatches matches = new() { Starts = [], Length = 0, Current = -1 };

    /// <summary>
    /// Gets or sets a value indicating whether the find bar is showing.
    /// </summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (Set(ref isOpen, value))
            {
                Search(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets the text to find.
    /// </summary>
    public string Query
    {
        get => query;
        set
        {
            if (Set(ref query, value))
            {
                Search(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether upper and lower case letters differ.
    /// </summary>
    public bool MatchCase
    {
        get => matchCase;
        set
        {
            if (Set(ref matchCase, value))
            {
                Search(true);
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether a match must be a whole word.
    /// </summary>
    public bool WholeWord
    {
        get => wholeWord;
        set
        {
            if (Set(ref wholeWord, value))
            {
                Search(true);
            }
        }
    }

    /// <summary>
    /// Gets the matches to show; empty while the bar is closed.
    /// </summary>
    public TextMatches Matches
    {
        get => matches;
        private set => Set(ref matches, value);
    }

    /// <summary>
    /// Gets the position of the current match and how many there are, or a note when there are none.
    /// </summary>
    public string Display => query.Length == 0 || !isOpen ? string.Empty : matches.Starts.Count == 0 ? "No results" : $"{matches.Current + 1} of {matches.Starts.Count}";

    /// <summary>
    /// Gets the command that goes to the next match.
    /// </summary>
    public AsyncCommand NextCommand { get; }

    /// <summary>
    /// Gets the command that goes to the previous match.
    /// </summary>
    public AsyncCommand PreviousCommand { get; }

    /// <summary>
    /// Gets the command that closes the find bar.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Sets the text searched, keeping the position in it when it changed under the user.
    /// </summary>
    /// <param name="text">The text, empty for none.</param>
    /// <param name="restart">Whether to go to the first match, as when another file is opened.</param>
    public void Show(string text, bool restart)
    {
        source = text;
        Search(restart, restart);
    }

    private Task Step(int direction)
    {
        int count = matches.Starts.Count;
        Matches = matches with { Current = (matches.Current + direction + count) % count, Navigate = true };
        Raise(nameof(Display));
        return Task.CompletedTask;
    }

    private bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void Search(bool restart, bool navigate = true)
    {
        List<int> starts = [];

        if (isOpen && query.Length > 0)
        {
            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            for (int at = source.IndexOf(query, comparison); at >= 0; at = source.IndexOf(query, at + query.Length, comparison))
            {
                if (!wholeWord || ((at == 0 || !IsWord(source[at - 1])) && (at + query.Length >= source.Length || !IsWord(source[at + query.Length]))))
                {
                    starts.Add(at);
                }
            }
        }

        int current = starts.Count == 0 ? -1 : restart ? 0 : Math.Min(Math.Max(matches.Current, 0), starts.Count - 1);
        Matches = new TextMatches { Starts = starts, Length = query.Length, Current = current, Navigate = navigate && current >= 0 };
        Raise(nameof(Display));
        NextCommand.Refresh();
        PreviousCommand.Refresh();
    }
}
