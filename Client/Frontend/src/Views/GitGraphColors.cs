namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The colors branch lines and their labels cycle through in the history graph.
/// </summary>
internal sealed class GitGraphColors
{
    /// <summary>
    /// Gets the colors in order.
    /// </summary>
    public IReadOnlyList<Color> All { get; } =
    [
        Color.Parse("#0085d9"), Color.Parse("#d9008f"), Color.Parse("#00d90a"), Color.Parse("#d98500"), Color.Parse("#a300d9"), Color.Parse("#ff0000"),
        Color.Parse("#00d9cc"), Color.Parse("#e138e8"), Color.Parse("#85d900"), Color.Parse("#dc5b23"), Color.Parse("#6f24d6"), Color.Parse("#ffcc00"),
    ];

    /// <summary>
    /// Gets the color of an index, wrapping around.
    /// </summary>
    /// <param name="index">The color index.</param>
    /// <returns>The color.</returns>
    public Color At(int index) => All[index % All.Count];
}
