namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Finds the syntax of a file's text, so it can be shown in color.
/// </summary>
internal interface ISyntaxHighlighter
{
    /// <summary>
    /// Highlights a file.
    /// </summary>
    /// <param name="path">The file's path, which says what language it is written in.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The highlighted stretches in order, none when the language is not recognized or the text is too large.</returns>
    IReadOnlyList<SyntaxToken> Highlight(string path, string text);
}

/// <inheritdoc />
internal sealed class SyntaxHighlighter(ISyntaxLanguages languages) : ISyntaxHighlighter
{
    private readonly int limit = 100_000;

    private readonly Regex number = new(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$", RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public IReadOnlyList<SyntaxToken> Highlight(string path, string text)
    {
        List<SyntaxToken> tokens = [];

        if (text.Length <= limit && languages.Detect(path, text) is { } language)
        {
            Scan(language, text, 0, text.Length, tokens);
        }

        return tokens;
    }

    private bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private bool Starts(string text, int index, int end, string marker) => marker.Length > 0 && index + marker.Length <= end && string.CompareOrdinal(text, index, marker, 0, marker.Length) == 0;

    private int LineEnd(string text, int index, int end)
    {
        int found = text.IndexOf('\n', index, end - index);
        return found < 0 ? end : found;
    }

    private void Scan(SyntaxLanguage language, string text, int start, int end, List<SyntaxToken> tokens)
    {
        if (language.Markup)
        {
            Markup(language, text, start, end, tokens);
        }
        else if (language.Delimiter is { } delimiter)
        {
            Delimited(delimiter, text, start, end, tokens);
        }
        else
        {
            Code(language, text, start, end, tokens);
        }
    }

    private void Code(SyntaxLanguage language, string text, int start, int end, List<SyntaxToken> tokens)
    {
        int i = start;
        bool lineStart = true;

        while (i < end)
        {
            char c = text[i];

            if (c == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (lineStart)
            {
                lineStart = false;

                if (Matched(language.Section, SyntaxKind.Tag, text, ref i, tokens) || Matched(language.LineKey, SyntaxKind.Property, text, ref i, tokens))
                {
                    continue;
                }
            }

            if (language.LineComments.FirstOrDefault(marker => Starts(text, i, end, marker)) is { } line)
            {
                int stop = LineEnd(text, i, end);
                tokens.Add(new SyntaxToken(SyntaxKind.Comment, i, stop - i));
                i = stop;
            }
            else if (language.BlockStart is { } block && Starts(text, i, end, block))
            {
                int close = text.IndexOf(language.BlockEnd, i + block.Length, end - i - block.Length, StringComparison.Ordinal);
                int stop = close < 0 ? end : close + language.BlockEnd.Length;
                tokens.Add(new SyntaxToken(SyntaxKind.Comment, i, stop - i));
                i = stop;
            }
            else if (Quoted(language, text, ref i, end, tokens))
            {
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < end && char.IsDigit(text[i + 1])))
            {
                int stop = i;

                if (c == '0' && i + 1 < end && "xXbB".Contains(text[i + 1]))
                {
                    stop += 2;
                }

                while (stop < end && (IsWord(text[stop]) || (text[stop] == '.' && stop + 1 < end && char.IsDigit(text[stop + 1]))))
                {
                    stop++;
                }

                tokens.Add(new SyntaxToken(SyntaxKind.Number, i, stop - i));
                i = stop;
            }
            else if (c == '#' && language.HexColors && HexColor(text, i, end) is > 0 and int colorLength)
            {
                tokens.Add(new SyntaxToken(SyntaxKind.Number, i, colorLength));
                i += colorLength;
            }
            else if (language.VariableSigils.Contains(c) && Variable(language, text, i, end) is > 0 and int variableLength)
            {
                tokens.Add(new SyntaxToken(SyntaxKind.Variable, i, variableLength));
                i += variableLength;
            }
            else if (Word(language, text, ref i, end, tokens))
            {
            }
            else
            {
                i++;
            }
        }
    }

    private bool Matched(Regex? pattern, SyntaxKind kind, string text, ref int i, List<SyntaxToken> tokens)
    {
        if (pattern?.Match(text, i) is { Success: true } match && match.Length > 0)
        {
            Group part = match.Groups[1].Success ? match.Groups[1] : match.Groups[0];
            tokens.Add(new SyntaxToken(kind, part.Index, part.Length));
            i = match.Index + match.Length;
            return true;
        }

        return false;
    }

    private bool Quoted(SyntaxLanguage language, string text, ref int i, int end, List<SyntaxToken> tokens)
    {
        char quote = text[i];

        if (!language.Quotes.Contains(quote) || (quote == '\'' && i > 0 && IsWord(text[i - 1])))
        {
            return false;
        }

        if (language.Lifetimes && quote == '\'' && i + 2 < end && char.IsLetter(text[i + 1]) && text[i + 2] != '\'')
        {
            int stop = i + 1;

            while (stop < end && IsWord(text[stop]))
            {
                stop++;
            }

            tokens.Add(new SyntaxToken(SyntaxKind.Type, i, stop - i));
            i = stop;
            return true;
        }

        int close;

        if (language.TripleQuotes && Starts(text, i, end, new string(quote, 3)))
        {
            int found = text.IndexOf(new string(quote, 3), i + 3, end - i - 3, StringComparison.Ordinal);
            close = found < 0 ? end : found + 3;
        }
        else
        {
            bool multiline = language.MultilineQuotes.Contains(quote);
            close = i + 1;

            while (close < end)
            {
                if (text[close] == '\\')
                {
                    close += 2;
                }
                else if (text[close] == quote)
                {
                    close++;
                    break;
                }
                else if (text[close] == '\n' && !multiline)
                {
                    break;
                }
                else
                {
                    close++;
                }
            }

            close = Math.Min(close, end);
        }

        bool key = false;

        if (language.QuotedKeys)
        {
            int after = close;

            while (after < end && text[after] is ' ' or '\t')
            {
                after++;
            }

            key = after < end && text[after] == ':';
        }

        tokens.Add(new SyntaxToken(key ? SyntaxKind.Property : SyntaxKind.String, i, close - i));
        i = close;
        return true;
    }

    private int HexColor(string text, int index, int end)
    {
        int stop = index + 1;

        while (stop < end && Uri.IsHexDigit(text[stop]))
        {
            stop++;
        }

        int digits = stop - index - 1;
        return digits is 3 or 4 or 6 or 8 ? digits + 1 : 0;
    }

    private int Variable(SyntaxLanguage language, string text, int index, int end)
    {
        char sigil = text[index];
        int next = index + 1;

        if (next < end && text[next] == sigil && sigil != '$')
        {
            next++;
        }

        if (next >= end)
        {
            return 0;
        }

        if (sigil == '$' && text[next] is '{' or '(')
        {
            int close = text.IndexOf(text[next] == '{' ? '}' : ')', next, LineEnd(text, next, end) - next);
            return close < 0 ? 0 : close + 1 - index;
        }

        int stop = next;

        while (stop < end && (IsWord(text[stop]) || (sigil == '$' && language.Name == "ps1" && text[stop] == ':' && stop + 1 < end && IsWord(text[stop + 1]))))
        {
            stop++;
        }

        return stop > next ? stop - index : 0;
    }

    private bool Word(SyntaxLanguage language, string text, ref int i, int end, List<SyntaxToken> tokens)
    {
        char c = text[i];
        bool prefixed = language.WordStart.Contains(c);

        if (prefixed)
        {
            if (i + 1 >= end || !(char.IsLetter(text[i + 1]) || (c == '-' && text[i + 1] == '-')))
            {
                return false;
            }
        }
        else if (!(char.IsLetter(c) || c == '_'))
        {
            return false;
        }

        int stop = i + 1;

        while (stop < end && (IsWord(text[stop]) || language.WordPart.Contains(text[stop])))
        {
            stop++;
        }

        string word = text[i..stop];
        SyntaxKind? kind = null;
        int peek = stop;

        while (peek < end && text[peek] is ' ' or '\t')
        {
            peek++;
        }

        if (language.Keywords.Contains(word))
        {
            kind = SyntaxKind.Keyword;
        }
        else if (c == '@' && prefixed)
        {
            kind = SyntaxKind.Type;
        }
        else if (language.ColonProperties && stop < end && text[stop] == ':' && stop + 1 < end && text[stop + 1] is ' ' or '\t' or '\n' or '\r')
        {
            kind = SyntaxKind.Property;
        }
        else if (language.Functions && peek < end && text[peek] == '(')
        {
            kind = SyntaxKind.Function;
        }
        else if (language.Types && char.IsUpper(word[0]) && word.Any(char.IsLower))
        {
            kind = SyntaxKind.Type;
        }

        if (kind is { } found)
        {
            tokens.Add(new SyntaxToken(found, i, stop - i));
        }

        i = stop;
        return true;
    }

    private void Delimited(char delimiter, string text, int start, int end, List<SyntaxToken> tokens)
    {
        int i = start;
        bool header = true;

        while (i < end)
        {
            char c = text[i];

            if (c == '\n')
            {
                header = false;
                i++;
            }
            else if (c == delimiter || c == '\r')
            {
                i++;
            }
            else if (c == '"')
            {
                int close = i + 1;

                while (close < end)
                {
                    if (text[close] == '"' && close + 1 < end && text[close + 1] == '"')
                    {
                        close += 2;
                    }
                    else if (text[close] == '"')
                    {
                        close++;
                        break;
                    }
                    else
                    {
                        close++;
                    }
                }

                tokens.Add(new SyntaxToken(header ? SyntaxKind.Property : SyntaxKind.String, i, close - i));
                i = close;
            }
            else
            {
                int stop = i;

                while (stop < end && text[stop] != delimiter && text[stop] != '\n' && text[stop] != '\r')
                {
                    stop++;
                }

                if (header)
                {
                    tokens.Add(new SyntaxToken(SyntaxKind.Property, i, stop - i));
                }
                else if (number.IsMatch(text.AsSpan(i, stop - i).Trim()))
                {
                    tokens.Add(new SyntaxToken(SyntaxKind.Number, i, stop - i));
                }

                i = stop;
            }
        }
    }

    private void Markup(SyntaxLanguage language, string text, int start, int end, List<SyntaxToken> tokens)
    {
        int i = start;

        while (i < end)
        {
            int open = text.IndexOf('<', i, end - i);

            if (open < 0)
            {
                return;
            }

            if (Starts(text, open, end, "<!--"))
            {
                int close = text.IndexOf("-->", open + 4, end - open - 4, StringComparison.Ordinal);
                i = close < 0 ? end : close + 3;
                tokens.Add(new SyntaxToken(SyntaxKind.Comment, open, i - open));
                continue;
            }

            if (Starts(text, open, end, "<![CDATA["))
            {
                int close = text.IndexOf("]]>", open + 9, end - open - 9, StringComparison.Ordinal);
                i = close < 0 ? end : close + 3;
                tokens.Add(new SyntaxToken(SyntaxKind.String, open, i - open));
                continue;
            }

            int nameStart = open + 1;
            bool closing = nameStart < end && text[nameStart] == '/';

            if (closing || (nameStart < end && text[nameStart] is '?' or '!'))
            {
                nameStart++;
            }

            if (nameStart >= end || !(char.IsLetter(text[nameStart]) || text[nameStart] == '_'))
            {
                i = open + 1;
                continue;
            }

            int j = nameStart;

            while (j < end && (IsWord(text[j]) || text[j] is ':' or '.' or '-'))
            {
                j++;
            }

            string name = text[nameStart..j];
            tokens.Add(new SyntaxToken(SyntaxKind.Tag, nameStart, j - nameStart));
            bool afterEquals = false;

            while (j < end && text[j] != '>')
            {
                char c = text[j];

                if (c is '"' or '\'')
                {
                    int close = text.IndexOf(c, j + 1, end - j - 1);
                    int stop = close < 0 ? end : close + 1;
                    tokens.Add(new SyntaxToken(SyntaxKind.String, j, stop - j));
                    j = stop;
                    afterEquals = false;
                }
                else if (c == '=')
                {
                    afterEquals = true;
                    j++;
                }
                else if (char.IsWhiteSpace(c) || c is '/' or '?')
                {
                    j++;
                }
                else
                {
                    int stop = j;

                    while (stop < end && !char.IsWhiteSpace(text[stop]) && text[stop] is not ('=' or '/' or '>' or '?' or '"' or '\''))
                    {
                        stop++;
                    }

                    tokens.Add(new SyntaxToken(afterEquals ? SyntaxKind.String : SyntaxKind.Property, j, stop - j));
                    j = stop;
                    afterEquals = false;
                }
            }

            bool selfClosing = j > 0 && j <= end && text[j - 1] == '/';
            j = Math.Min(j + 1, end);
            i = j;

            if (language.EmbedsCode && !closing && !selfClosing && (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                int close = text.IndexOf("</" + name, j, end - j, StringComparison.OrdinalIgnoreCase);
                int stop = close < 0 ? end : close;

                if (languages.Find(name.Equals("script", StringComparison.OrdinalIgnoreCase) ? "js" : "css") is { } inner)
                {
                    Scan(inner, text, j, stop, tokens);
                }

                i = stop;
            }
        }
    }
}
