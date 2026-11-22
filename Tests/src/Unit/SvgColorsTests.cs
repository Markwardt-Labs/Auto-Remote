namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SvgColorsTests
{
    private readonly SvgColors colors = new(new Dictionary<string, string> { ["--bg"] = "#000000", ["--fg"] = "#FFFFFF", ["--muted"] = "#102030" });

    [Theory]
    [InlineData("fill=\"color-mix(in srgb, var(--fg) 50%, var(--bg))\"", "fill=\"#808080\"")]
    [InlineData("fill=\"color-mix(in srgb, #000000 25%, #FFFFFF)\"", "fill=\"#BFBFBF\"")]
    [InlineData("fill=\"color-mix(in srgb, var(--fg) 25%, black 75%)\"", "fill=\"#404040\"")]
    [InlineData("fill=\"color-mix(in srgb, color-mix(in srgb, var(--fg) 50%, var(--bg)) 100%, #000)\"", "fill=\"#808080\"")]
    [InlineData("fill=\"var(--fg)\"", "fill=\"#FFFFFF\"")]
    [InlineData("fill=\"var(--missing, #112233)\"", "fill=\"#112233\"")]
    [InlineData("fill=\"var(--muted, color-mix(in srgb, var(--fg) 50%, var(--bg)))\"", "fill=\"#102030\"")]
    [InlineData("fill=\"var(--missing, color-mix(in srgb, var(--fg) 50%, var(--bg)))\"", "fill=\"#808080\"")]
    [InlineData("shadow(color-mix(in srgb, #FF0000 50%, transparent))", "shadow(rgba(255,0,0,0.5))")]
    public void Resolve_ReplacesColorFunctionsWithTheColorTheyStandFor(string svg, string expected) => Assert.Equal(expected, colors.Resolve(svg));

    [Fact]
    public void Resolve_KeepsReferencesToDerivedVariablesAndFixesTheirDefinitions()
    {
        string svg = colors.Resolve("<style>svg { --_group: color-mix(in srgb, var(--fg) 50%, var(--bg)); --_text: var(--fg); }</style><rect fill=\"var(--_group)\" stroke=\"var(--_text)\"/>");

        Assert.Equal("<style>svg { --_group: #808080; --_text: #FFFFFF; }</style><rect fill=\"var(--_group)\" stroke=\"var(--_text)\"/>", svg);
    }

    [Fact]
    public void Resolve_FollowsDerivedVariablesInsideColorFunctions()
    {
        string svg = colors.Resolve("svg { --_a: #000000; } rect { fill: color-mix(in srgb, var(--_a) 50%, var(--fg)); }");

        Assert.Equal("svg { --_a: #000000; } rect { fill: #808080; }", svg);
    }

    [Theory]
    [InlineData("fill=\"color-mix(in oklch, red, blue)\"")]
    [InlineData("fill=\"color-mix(in srgb, var(--unknown) 50%, var(--bg))\"")]
    [InlineData("fill=\"var(--unknown)\"")]
    [InlineData("fill=\"color-mix(in srgb, #000000 50%\"")]
    public void Resolve_LeavesWhatItCannotEvaluateAsItIs(string svg) => Assert.Equal(svg, colors.Resolve(svg));

    [Fact]
    public void Resolve_LeavesASvgWithoutColorFunctionsUntouched()
    {
        string svg = "<svg><rect fill=\"#123456\" width=\"10\"/></svg>";

        Assert.Equal(svg, colors.Resolve(svg));
    }
}
