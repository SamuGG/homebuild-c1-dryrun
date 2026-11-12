using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace HomeBuild.Web.Tests;

/// <summary>Reads a rendered form the way a browser would: its hidden fields and the names of its inputs.</summary>
internal static class HtmlForm
{
    private static readonly HtmlParser Parser = new();

    public static IHtmlDocument Parse(string html) => Parser.ParseDocument(html);

    /// <summary>The form's fields (hidden ones keep their values) with <paramref name="values"/> set by input id.</summary>
    public static Dictionary<string, string> Fields(string html, string formTestId, IReadOnlyDictionary<string, string> values, bool withAntiforgery = true)
    {
        var form = Parse(html).QuerySelector($"form[data-testid={formTestId}]") ?? throw new InvalidOperationException($"No form {formTestId}.");
        var fields = new Dictionary<string, string>();
        foreach (var input in form.QuerySelectorAll("input[type=hidden]").Cast<IHtmlInputElement>())
        {
            fields[input.Name!] = input.Value;
        }

        foreach (var (id, value) in values)
        {
            var element = form.QuerySelector($"#{id}") ?? throw new InvalidOperationException($"No input #{id}.");
            fields[element.GetAttribute("name")!] = value;
        }

        if (!withAntiforgery)
        {
            fields.Remove("__RequestVerificationToken");
        }

        return fields;
    }

    /// <summary>
    /// The validation messages shown inside the element that holds input #<paramref name="inputId"/>. .NET 11 renders an
    /// empty, hidden ValidationMessage placeholder (data-valmsg-for) even with no message; those are skipped.
    /// </summary>
    public static string[] MessagesFor(string html, string inputId) =>
        Parse(html).QuerySelector($"#{inputId}")?.ParentElement?.QuerySelectorAll(".validation-message")
            .Where(m => !m.HasAttribute("hidden") && m.TextContent.Trim().Length > 0)
            .Select(m => m.TextContent.Trim()).ToArray() ?? [];

    public static string[] Summary(string html) =>
        Parse(html).QuerySelectorAll("ul[data-valmsg-summary] li").Select(m => m.TextContent.Trim()).ToArray();
}
