namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A highlighted stretch of source text; text between tokens is plain.
/// </summary>
/// <param name="Kind">The category of the stretch.</param>
/// <param name="Start">The index of its first character.</param>
/// <param name="Length">The number of characters it covers.</param>
internal readonly record struct SyntaxToken(SyntaxKind Kind, int Start, int Length);
