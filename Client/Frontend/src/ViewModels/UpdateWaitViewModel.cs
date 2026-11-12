namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The updating screen of a client that was restarted by its own update and cannot reach its server yet: it shows the same screen as the update itself until the server answers.
/// </summary>
internal sealed class UpdateWaitViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateWaitViewModel" /> class.
    /// </summary>
    public UpdateWaitViewModel() => CloseCommand = new AsyncCommand(_ =>
    {
        Closed?.Invoke();
        return Task.CompletedTask;
    });

    /// <summary>
    /// Raised when the user leaves the screen before the server is back.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Gets the command that leaves the screen.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Gets what the update is waiting for.
    /// </summary>
    public string Status => "The server restarts. This closes once it is back.";

    /// <summary>
    /// Gets the fraction of the download that is done, which is none because everything was downloaded.
    /// </summary>
    public double Progress => 0;

    /// <summary>
    /// Gets a value indicating whether the progress bar only shows that something is going on.
    /// </summary>
    public bool IsProgressUnknown => true;

    /// <summary>
    /// Gets a value indicating whether the progress bar shows.
    /// </summary>
    public bool IsWorking => true;

    /// <summary>
    /// Gets a value indicating whether the screen may be left.
    /// </summary>
    public bool CanClose => true;

    /// <summary>
    /// Gets the label of the button that leaves the screen.
    /// </summary>
    public string CloseText => "Close";
}
