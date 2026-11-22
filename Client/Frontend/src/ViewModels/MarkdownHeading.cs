namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A heading of a rendered markdown document, as the outline, the breadcrumb and the folding by level know it.
/// </summary>
internal sealed record MarkdownHeading
{
    /// <summary>
    /// Gets the position of the heading among the headings of the document, in reading order.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Gets the heading's text without formatting.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the level the document gave the heading, 1 for <c>#</c> to 6 for <c>######</c>.
    /// </summary>
    public required int Level { get; init; }

    /// <summary>
    /// Gets how many headings it is nested under, which differs from <see cref="Level" /> when the document skips levels.
    /// </summary>
    public required int Depth { get; init; }

    /// <summary>
    /// Gets the section number made from the position of the heading among its siblings and of its parents among theirs, such as <c>2.1.3</c>; empty for the title of a document whose only heading of the top level is that one, which its sections are then numbered under.
    /// </summary>
    public required string Number { get; init; }

    /// <summary>
    /// Gets the <see cref="Index" /> of the heading it is nested under, or -1 for a heading of the top level.
    /// </summary>
    public required int Parent { get; init; }

    /// <summary>
    /// Gets a value indicating whether the heading is nested under another one.
    /// </summary>
    public bool HasParent => Parent >= 0;

    /// <summary>
    /// Gets the number and the title as the outline lists them.
    /// </summary>
    public string Label => Number.Length == 0 ? Title : $"{Number}  {Title}";

    /// <summary>
    /// Gets the indentation the outline gives the heading, a step for every level it is nested under.
    /// </summary>
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
}
