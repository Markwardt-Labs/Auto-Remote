namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The category of a highlighted stretch of source text.
/// </summary>
internal enum SyntaxKind
{
    /// <summary>
    /// A language keyword, literal or preprocessor directive.
    /// </summary>
    Keyword,

    /// <summary>
    /// A string literal, or an attribute value in markup.
    /// </summary>
    String,

    /// <summary>
    /// A comment.
    /// </summary>
    Comment,

    /// <summary>
    /// A numeric literal.
    /// </summary>
    Number,

    /// <summary>
    /// A name followed by an opening parenthesis.
    /// </summary>
    Function,

    /// <summary>
    /// A type name or annotation.
    /// </summary>
    Type,

    /// <summary>
    /// A key, attribute name or header field.
    /// </summary>
    Property,

    /// <summary>
    /// A markup tag name or a section heading.
    /// </summary>
    Tag,

    /// <summary>
    /// A variable reference such as <c>$name</c>.
    /// </summary>
    Variable
}
