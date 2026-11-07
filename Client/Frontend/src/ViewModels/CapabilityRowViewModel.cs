namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One row of the environment settings: the model a capability uses and, for local models, how many tokens of context it is run with.
/// </summary>
/// <param name="name">The capability the row is for.</param>
internal sealed class CapabilityRowViewModel(string name) : ViewModelBase
{
    private string model = string.Empty;

    private decimal? context;

    /// <summary>
    /// Gets the name of the capability.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets the name of the model; empty when none is chosen.
    /// </summary>
    public string Model
    {
        get => model;
        set => Set(ref model, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the context size in tokens, or <see langword="null" /> to let the tool decide.
    /// </summary>
    public decimal? Context
    {
        get => context;
        set => Set(ref context, value);
    }
}
