namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Base for bindable view models: change notification plus busy and error state for server calls.
/// </summary>
internal abstract class ViewModelBase : INotifyPropertyChanged
{
    private bool isBusy;

    private string? error;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets a value indicating whether a server call is in progress.
    /// </summary>
    public bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    /// <summary>
    /// Gets the message of the last failed server call, or <see langword="null" />.
    /// </summary>
    public string? Error
    {
        get => error;
        protected set
        {
            if (Set(ref error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="Error" /> is set.
    /// </summary>
    public bool HasError => error is not null;

    /// <summary>
    /// Sets a field and raises <see cref="PropertyChanged" /> when the value changed.
    /// </summary>
    /// <typeparam name="T">The field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">The property name.</param>
    /// <returns><see langword="true" /> if the value changed.</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    /// <summary>
    /// Raises <see cref="PropertyChanged" />.
    /// </summary>
    /// <param name="name">The property name.</param>
    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Runs a server call, tracking <see cref="IsBusy" /> and turning connection or server failures into <see cref="Error" />.
    /// </summary>
    /// <param name="action">The call.</param>
    /// <returns><see langword="true" /> if the call succeeded.</returns>
    protected async Task<bool> Attempt(Func<Task> action)
    {
        IsBusy = true;
        Error = null;

        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            Error = exception.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
