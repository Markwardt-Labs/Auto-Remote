namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Describes how to highlight one family of source files.
/// </summary>
internal sealed record SyntaxLanguage
{
    /// <summary>
    /// Gets the language's name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the words highlighted as keywords.
    /// </summary>
    public IReadOnlySet<string> Keywords { get; init; } = new HashSet<string>();

    /// <summary>
    /// Gets the markers that start a comment running to the end of the line.
    /// </summary>
    public IReadOnlyList<string> LineComments { get; init; } = [];

    /// <summary>
    /// Gets the marker that starts a block comment, or <see langword="null" /> when there are none.
    /// </summary>
    public string? BlockStart { get; init; }

    /// <summary>
    /// Gets the marker that ends a block comment.
    /// </summary>
    public string BlockEnd { get; init; } = string.Empty;

    /// <summary>
    /// Gets the characters that quote a string ending at the end of the line.
    /// </summary>
    public string Quotes { get; init; } = "\"'";

    /// <summary>
    /// Gets the quote characters whose strings may span lines.
    /// </summary>
    public string MultilineQuotes { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether tripled quotes start a string spanning lines.
    /// </summary>
    public bool TripleQuotes { get; init; }

    /// <summary>
    /// Gets the extra characters a word may start with, when followed by a letter.
    /// </summary>
    public string WordStart { get; init; } = string.Empty;

    /// <summary>
    /// Gets the extra characters a word may contain.
    /// </summary>
    public string WordPart { get; init; } = string.Empty;

    /// <summary>
    /// Gets the characters that start a variable reference.
    /// </summary>
    public string VariableSigils { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether a name followed by an opening parenthesis is a function.
    /// </summary>
    public bool Functions { get; init; }

    /// <summary>
    /// Gets a value indicating whether capitalized names are types.
    /// </summary>
    public bool Types { get; init; }

    /// <summary>
    /// Gets a value indicating whether a string followed by a colon is a key.
    /// </summary>
    public bool QuotedKeys { get; init; }

    /// <summary>
    /// Gets a value indicating whether a name followed by a colon and a space is a property.
    /// </summary>
    public bool ColonProperties { get; init; }

    /// <summary>
    /// Gets a value indicating whether <c>#rrggbb</c> is a number.
    /// </summary>
    public bool HexColors { get; init; }

    /// <summary>
    /// Gets a value indicating whether a quote followed by a letter and no closing quote is a lifetime, not a string.
    /// </summary>
    public bool Lifetimes { get; init; }

    /// <summary>
    /// Gets a value indicating whether the text is angle bracket markup.
    /// </summary>
    public bool Markup { get; init; }

    /// <summary>
    /// Gets a value indicating whether the content of script and style elements is highlighted as their own language.
    /// </summary>
    public bool EmbedsCode { get; init; }

    /// <summary>
    /// Gets the field separator when the text is delimited rows whose first row is a header.
    /// </summary>
    public char? Delimiter { get; init; }

    /// <summary>
    /// Gets the pattern for a section heading at the start of a line.
    /// </summary>
    public Regex? Section { get; init; }

    /// <summary>
    /// Gets the pattern for the key at the start of a line; its first group, when present, is the highlighted part and ends the match.
    /// </summary>
    public Regex? LineKey { get; init; }
}
