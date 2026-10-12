namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A command that runs an asynchronous action and stays disabled while that action is running.
/// </summary>
/// <param name="execute">The action to run; it receives the command parameter.</param>
/// <param name="canExecute">Whether the command is currently allowed, if it is ever not.</param>
internal sealed class AsyncCommand(Func<object?, Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        running = true;
        Refresh();

        try
        {
            await execute(parameter);
        }
        finally
        {
            running = false;
            Refresh();
        }
    }

    /// <summary>
    /// Re-evaluates whether the command may run.
    /// </summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
