namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A command that runs an asynchronous action and, unless told otherwise, stays disabled while that action is running.
/// </summary>
/// <param name="execute">The action to run; it receives the command parameter.</param>
/// <param name="canExecute">Whether the command is currently allowed, if it is ever not.</param>
/// <param name="disableWhileRunning"><see langword="false" /> for a command that one button per list entry shares: every entry would otherwise look disabled while the action of one of them runs, so the action has to ignore a second call itself.</param>
internal sealed class AsyncCommand(Func<object?, Task> execute, Func<bool>? canExecute = null, bool disableWhileRunning = true) : ICommand
{
    private bool running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => (!running || !disableWhileRunning) && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        running = true;

        if (disableWhileRunning)
        {
            Refresh();
        }

        try
        {
            await execute(parameter);
        }
        finally
        {
            running = false;

            if (disableWhileRunning)
            {
                Refresh();
            }
        }
    }

    /// <summary>
    /// Re-evaluates whether the command may run.
    /// </summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
