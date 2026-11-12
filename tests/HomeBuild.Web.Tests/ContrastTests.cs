using System.Globalization;
using System.Text.RegularExpressions;

namespace HomeBuild.Web.Tests;

/// <summary>WCAG 2.2 AA contrast for every text/background pair the components use, computed from the @theme tokens.</summary>
public sealed partial class ContrastTests
{
    private static readonly Lazy<Dictionary<string, (double R, double G, double B)>> Tokens = new(ReadTokens);

    // (text, background) as the components use them; "white" is #fff.
    public static TheoryData<string, string> TextPairs => new()
    {
        { "brand-900", "brand-50" },   // body text on the page
        { "brand-700", "brand-50" },   // links and status text on the page
        { "brand-700", "white" },      // links in cards and tables
        { "brand-900", "white" },      // card and table text
        { "white", "brand-700" },      // header, primary button
        { "white", "brand-900" },      // primary button hover
        { "brand-900", "brand-100" },  // table header, Success badge, secondary hover
        { "brand-900", "accent-500" }, // Warning badge
        { "danger-600", "white" },     // validation messages
        { "white", "danger-600" },     // error boundary
    };

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_tokens_meet_AA_contrast(string text, string background)
    {
        var ratio = Contrast(Colour(text), Colour(background));

        Assert.True(ratio >= 4.5, $"{text} on {background}: {ratio:F2}:1, below 4.5:1");
    }

    private static (double R, double G, double B) Colour(string name) => name == "white" ? (1, 1, 1) : Tokens.Value[name];

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        static double L((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B; // linear sRGB
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Dictionary<string, (double, double, double)> ReadTokens()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        var css = File.ReadAllText(Path.Combine(dir.FullName, "src/HomeBuild.Web/Styles/app.css"));
        return OklchToken().Matches(css).ToDictionary(
            m => m.Groups["name"].Value,
            m => OklchToLinearSrgb(Number(m, "l"), Number(m, "c"), Number(m, "h")));
    }

    private static double Number(Match m, string group) => double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    // OKLCH -> OKLab -> linear sRGB (Björn Ottosson's matrices, as in CSS Color 4), clamped to the sRGB gamut.
    private static (double, double, double) OklchToLinearSrgb(double l, double c, double hDegrees)
    {
        var h = hDegrees * Math.PI / 180;
        var (a, b) = (c * Math.Cos(h), c * Math.Sin(h));
        var l1 = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m1 = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s1 = Math.Pow(l - 0.0894841775 * a - 1.2914855480 * b, 3);
        static double Clamp(double v) => Math.Clamp(v, 0, 1);
        return (
            Clamp(4.0767416621 * l1 - 3.3077115913 * m1 + 0.2309699292 * s1),
            Clamp(-1.2684380046 * l1 + 2.6097574011 * m1 - 0.3413193965 * s1),
            Clamp(-0.0041960863 * l1 - 0.7034186147 * m1 + 1.7076147010 * s1));
    }

    [GeneratedRegex(@"--color-(?<name>[a-z]+-\d+):\s*oklch\((?<l>[\d.]+)\s+(?<c>[\d.]+)\s+(?<h>[\d.]+)\)")]
    private static partial Regex OklchToken();
}
