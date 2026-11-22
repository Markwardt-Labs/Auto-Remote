namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A diagram drawn as a picture.
/// </summary>
internal sealed record DiagramPicture
{
    /// <summary>
    /// Gets the picture, sharper than <see cref="Width" /> and <see cref="Height" /> ask for so it stays crisp on a screen with many pixels per unit.
    /// </summary>
    public required Avalonia.Media.Imaging.Bitmap Image { get; init; }

    /// <summary>
    /// Gets the width the diagram is shown at, in device independent units.
    /// </summary>
    public required double Width { get; init; }

    /// <summary>
    /// Gets the height the diagram is shown at, in device independent units.
    /// </summary>
    public required double Height { get; init; }

    /// <summary>
    /// Gets how many pixels of <see cref="Image" /> make up one unit of <see cref="Width" /> and <see cref="Height" />.
    /// </summary>
    public required double Scale { get; init; }
}

/// <summary>
/// Supplies the pictures of the Mermaid diagrams a markdown file describes: the server draws them as SVG, which is the same for every client, and the client turns that into a picture.
/// </summary>
internal interface IDiagramSource
{
    /// <summary>
    /// Looks for a diagram that was drawn already, so a screen that is built again shows it at once.
    /// </summary>
    /// <param name="source">The Mermaid text.</param>
    /// <returns>The picture, or <see langword="null" /> when the diagram was not drawn yet.</returns>
    DiagramPicture? Find(string source);

    /// <summary>
    /// Draws a diagram, asking the server unless it was drawn before; a diagram that is being drawn is asked for once however many screens wait for it.
    /// </summary>
    /// <param name="source">The Mermaid text.</param>
    /// <param name="cancellation">Stops the wait.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="RemoteException">The server could not draw the diagram.</exception>
    /// <exception cref="InvalidDataException">The SVG the server sent cannot be turned into a picture.</exception>
    Task<DiagramPicture> Draw(string source, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class DiagramSource(IServerConnection connection) : IDiagramSource
{
    private readonly ConcurrentDictionary<string, DiagramPicture> drawn = new();

    private readonly ConcurrentDictionary<string, Task<DiagramPicture>> drawing = new();

    private readonly int maxPictures = 16;

    private readonly double sharpness = 3;

    private readonly double largestSide = 4096;

    /// <inheritdoc />
    public DiagramPicture? Find(string source) => drawn.TryGetValue(Key(source), out DiagramPicture? picture) ? picture : null;

    /// <inheritdoc />
    public async Task<DiagramPicture> Draw(string source, CancellationToken cancellation)
    {
        string key = Key(source);

        if (drawn.TryGetValue(key, out DiagramPicture? known))
        {
            return known;
        }

        Task<DiagramPicture> task = drawing.GetOrAdd(key, _ => Ask(key, source));

        try
        {
            return await task.WaitAsync(cancellation);
        }
        finally
        {
            if (task.IsCompleted)
            {
                drawing.TryRemove(new KeyValuePair<string, Task<DiagramPicture>>(key, task));
            }
        }
    }

    private string Key(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim())));

    private async Task<DiagramPicture> Ask(string key, string source)
    {
        string svg = await connection.RenderDiagram(source, CancellationToken.None).ConfigureAwait(false);
        DiagramPicture picture = await Task.Run(() => Picture(svg)).ConfigureAwait(false);

        if (drawn.Count >= maxPictures)
        {
            drawn.Clear();
        }

        drawn[key] = picture;
        return picture;
    }

    private DiagramPicture Picture(string svg)
    {
        try
        {
            using Svg.Skia.SKSvg document = new();

            if (document.FromSvg(svg) is null || document.Picture is not { } drawing)
            {
                throw new InvalidDataException("The server sent a diagram that is not an SVG.");
            }

            SkiaSharp.SKRect bounds = drawing.CullRect;

            if (bounds.Width < 1 || bounds.Height < 1)
            {
                throw new InvalidDataException("The diagram is empty.");
            }

            float scale = (float)Math.Min(sharpness, largestSide / Math.Max(bounds.Width, bounds.Height));
            using MemoryStream stream = new();
            Svg.Skia.SKPictureExtensions.ToImage(drawing, stream, SkiaSharp.SKColors.Transparent, SkiaSharp.SKEncodedImageFormat.Png, 100, scale, scale, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul, SkiaSharp.SKColorSpace.CreateSrgb());
            stream.Position = 0;
            return new DiagramPicture { Image = new Avalonia.Media.Imaging.Bitmap(stream), Width = bounds.Width, Height = bounds.Height, Scale = scale };
        }
        catch (Exception exception) when (exception is not InvalidDataException and (ArgumentException or InvalidOperationException or NullReferenceException or FormatException or System.Xml.XmlException))
        {
            throw new InvalidDataException($"The diagram cannot be drawn: {exception.Message}", exception);
        }
    }
}
