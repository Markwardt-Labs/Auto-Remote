namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The short line under a working workspace in the workspace list that says what it takes. It changes on its own, so the row it is in stays as it is.
/// </summary>
internal sealed class ResourceBadge : ViewModelBase
{
    private string text = string.Empty;

    /// <summary>
    /// Gets or sets the line, empty when there is nothing to say.
    /// </summary>
    public string Text
    {
        get => text;
        set
        {
            if (Set(ref text, value))
            {
                Raise(nameof(HasText));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether there is anything to say.
    /// </summary>
    public bool HasText => text.Length > 0;
}
