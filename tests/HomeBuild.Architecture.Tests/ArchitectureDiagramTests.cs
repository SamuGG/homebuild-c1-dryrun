using System.Reflection;
using System.Text.RegularExpressions;

namespace HomeBuild.Architecture.Tests;

/// <summary>
/// C7 Lab 7: the component diagram (docs/architecture/components.md) shows exactly the API's modules. A module added in
/// code without a diagram update fails CI, and so does a diagram box with no module behind it.
/// </summary>
public sealed partial class ArchitectureDiagramTests
{
    [Fact]
    public void Component_diagram_shows_every_module_and_only_real_ones()
    {
        var inCode = ModulesInCode();
        var inDiagram = ModulesInDiagram();

        Assert.True(inCode.Count >= 4, $"Found only {inCode.Count} modules in HomeBuild.Api; the discovery rule is broken.");
        var missing = inCode.Except(inDiagram).ToList();
        var invented = inDiagram.Except(inCode).ToList();
        Assert.True(missing.Count == 0,
            $"Module(s) {string.Join(", ", missing)} exist in HomeBuild.Api but not in docs/architecture/components.md. Add a box to the 'modules' subgraph.");
        Assert.True(invented.Count == 0,
            $"docs/architecture/components.md shows {string.Join(", ", invented)}, which HomeBuild.Api does not have. Remove or rename the box.");
    }

    /// <summary>A module is a static class in HomeBuild.Api.Modules.{Name} with a public const Name (C2's convention).</summary>
    private static List<string> ModulesInCode() =>
        [.. typeof(HomeBuild.Api.Modules.Catalogue.CatalogueModule).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: true, IsSealed: true, IsPublic: true } && t.Name.EndsWith("Module", StringComparison.Ordinal)
                && t.Namespace?.StartsWith("HomeBuild.Api.Modules.", StringComparison.Ordinal) == true)
            .Select(t => t.GetField("Name", BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field ? (string)field.GetRawConstantValue()! : null)
            .OfType<string>()
            .Order(StringComparer.Ordinal)];

    /// <summary>The node IDs inside the diagram's <c>subgraph modules</c> block.</summary>
    private static List<string> ModulesInDiagram()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "architecture", "components.md"));
        var block = SubgraphBlock().Match(text);
        Assert.True(block.Success, "docs/architecture/components.md has no 'subgraph modules [...]' ... 'end' block.");
        return [.. NodeId().Matches(block.Groups["body"].Value).Select(m => m.Groups["id"].Value).Order(StringComparer.Ordinal)];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HomeBuild.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HomeBuild.slnx not found above the test output.");
    }

    [GeneratedRegex(@"subgraph modules\b[^\n]*\n(?<body>.*?)\n\s*end\b", RegexOptions.Singleline)]
    private static partial Regex SubgraphBlock();

    [GeneratedRegex(@"^\s*(?<id>[A-Za-z][A-Za-z0-9]*)\s*\[", RegexOptions.Multiline)]
    private static partial Regex NodeId();
}
