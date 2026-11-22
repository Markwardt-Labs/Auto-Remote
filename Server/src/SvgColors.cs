namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Turns the CSS color functions of a Mermaid SVG into plain colors. Mermaid's themes derive most shades (group fills, shape gradients, shadows) with <c>color-mix()</c> and <c>var()</c>, which browsers evaluate but the SVG renderer of the Avalonia clients does not, so shapes that use them were drawn black.
/// </summary>
internal interface ISvgColors
{
    /// <summary>
    /// Replaces every <c>color-mix()</c> and every <c>var()</c> of a theme color in an SVG with the color it stands for. References to the SVG's own derived variables (those starting with <c>--_</c>) stay, since their definitions are plain colors afterwards.
    /// </summary>
    /// <param name="svg">The SVG Mermaid drew.</param>
    /// <returns>The same SVG with fixed colors.</returns>
    string Resolve(string svg);
}

/// <inheritdoc />
internal sealed class SvgColors(IReadOnlyDictionary<string, string> palette) : ISvgColors
{
    private readonly int maxDepth = 16;

    private readonly Regex declaration = new(@"(--_[\w-]+)\s*:\s*([^;}]+);", RegexOptions.CultureInvariant);

    private readonly Regex weighted = new(@"^(.*\S)\s+([\d.]+)%$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public string Resolve(string svg)
    {
        Dictionary<string, string> derived = [];

        foreach (Match match in declaration.Matches(svg))
        {
            derived[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        StringBuilder result = new();
        int position = 0;

        while (position < svg.Length)
        {
            int next = Next(svg, position);

            if (next < 0)
            {
                result.Append(svg, position, svg.Length - position);
                break;
            }

            result.Append(svg, position, next - position);
            int close = Close(svg, svg.IndexOf('(', next));

            if (close < 0)
            {
                result.Append(svg, next, svg.Length - next);
                break;
            }

            string expression = svg.Substring(next, close - next + 1);
            result.Append(Evaluate(expression, derived, 0)?.ToString() ?? expression);
            position = close + 1;
        }

        return result.ToString();
    }

    private int Next(string text, int from)
    {
        int mix = text.IndexOf("color-mix(", from, StringComparison.Ordinal);
        int reference = from;

        while ((reference = text.IndexOf("var(--", reference, StringComparison.Ordinal)) >= 0 && text[reference + 6] == '_')
        {
            reference += 6;
        }

        return mix < 0 ? reference : reference < 0 ? mix : Math.Min(mix, reference);
    }

    private int Close(string text, int open)
    {
        int depth = 0;

        for (int index = open; index >= 0 && index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private List<string> Split(string text)
    {
        List<string> parts = [];
        int depth = 0;
        int start = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;
            }
            else if (text[index] == ',' && depth == 0)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private Rgba? Evaluate(string expression, Dictionary<string, string> derived, int depth)
    {
        string text = expression.Trim();

        if (depth > maxDepth)
        {
            return null;
        }

        if (text.StartsWith("var(", StringComparison.Ordinal))
        {
            List<string> parts = Split(text[4..^1]);
            string name = parts[0].Trim();
            string? value = palette.TryGetValue(name, out string? known) ? known : derived.TryGetValue(name, out string? own) ? own : null;

            if (value is not null && Evaluate(value, derived, depth + 1) is { } found)
            {
                return found;
            }

            return parts.Count > 1 ? Evaluate(string.Join(',', parts.Skip(1)), derived, depth + 1) : null;
        }

        if (text.StartsWith("color-mix(", StringComparison.Ordinal))
        {
            List<string> parts = Split(text[10..^1]);

            if (parts.Count != 3 || !parts[0].Trim().Equals("in srgb", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            (Rgba? first, double? firstShare) = Weigh(parts[1], derived, depth);
            (Rgba? second, double? secondShare) = Weigh(parts[2], derived, depth);

            if (first is null || second is null)
            {
                return null;
            }

            double firstWeight = firstShare ?? (secondShare is { } other ? 100 - other : 50);
            double secondWeight = secondShare ?? (100 - firstWeight);
            return first.Value.Mix(firstWeight / 100, second.Value, secondWeight / 100);
        }

        return Rgba.Parse(text);
    }

    private (Rgba? Color, double? Share) Weigh(string part, Dictionary<string, string> derived, int depth)
    {
        string text = part.Trim();
        Match match = weighted.Match(text);

        return match.Success
            ? (Evaluate(match.Groups[1].Value, derived, depth + 1), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : (Evaluate(text, derived, depth + 1), null);
    }

    private readonly record struct Rgba(double Red, double Green, double Blue, double Alpha)
    {
        public static Rgba? Parse(string text)
        {
            string value = text.Trim().ToLowerInvariant();

            return value switch
            {
                "transparent" => new Rgba(0, 0, 0, 0),
                "black" => new Rgba(0, 0, 0, 1),
                "white" => new Rgba(255, 255, 255, 1),
                _ when value.Length == 7 && value[0] == '#' && int.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number) => new Rgba((number >> 16) & 255, (number >> 8) & 255, number & 255, 1),
                _ when value.Length == 4 && value[0] == '#' && int.TryParse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int short3) => new Rgba(((short3 >> 8) & 15) * 17, ((short3 >> 4) & 15) * 17, (short3 & 15) * 17, 1),
                _ => null,
            };
        }

        public Rgba Mix(double weight, Rgba other, double otherWeight)
        {
            double ownAlpha = Alpha * weight;
            double otherAlpha = other.Alpha * otherWeight;
            double alpha = ownAlpha + otherAlpha;

            return alpha <= 0
                ? new Rgba(0, 0, 0, 0)
                : new Rgba((Red * ownAlpha + other.Red * otherAlpha) / alpha, (Green * ownAlpha + other.Green * otherAlpha) / alpha, (Blue * ownAlpha + other.Blue * otherAlpha) / alpha, Math.Min(1, alpha));
        }

        public override string ToString() => Alpha >= 0.999
            ? $"#{(int)Math.Round(Red):X2}{(int)Math.Round(Green):X2}{(int)Math.Round(Blue):X2}"
            : string.Create(CultureInfo.InvariantCulture, $"rgba({(int)Math.Round(Red)},{(int)Math.Round(Green)},{(int)Math.Round(Blue)},{Math.Round(Alpha, 3)})");
    }
}
