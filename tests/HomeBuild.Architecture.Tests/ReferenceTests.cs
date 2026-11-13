using System.Reflection;
using System.Xml.Linq;

namespace HomeBuild.Architecture.Tests;

/// <summary>
/// Which project may depend on which (C6 Lab 4). The compiler enforces direction once projects are separate; these
/// tests catch what it cannot: a new reference that the build would happily accept.
/// </summary>
public sealed class ReferenceTests
{
    private sealed record Rule(Assembly Assembly, string[] HomeBuild, string[] Packages);

    // Allowed references per production assembly: our own projects exactly, other packages by name prefix
    // ("*" = any package). The base library (System.*, netstandard) is always allowed.
    private static readonly Rule[] Rules =
    [
        new(typeof(HomeBuild.Domain.Money).Assembly, [], []),
        new(typeof(HomeBuild.Payments.DepositPayments).Assembly, ["HomeBuild.Domain"], []),
        new(typeof(HomeBuild.Payments.Infrastructure.PaymentsInfrastructure).Assembly,
            ["HomeBuild.Domain", "HomeBuild.Payments.Core"], ["Microsoft.EntityFrameworkCore", "Microsoft.Extensions.", "Npgsql"]),
        new(typeof(HomeBuild.Api.Modules.Catalogue.CatalogueModule).Assembly, ["HomeBuild.Domain"], ["*"]),
        new(typeof(HomeBuild.Web.Components.App).Assembly, [], ["Microsoft.AspNetCore", "Microsoft.Extensions.", "Microsoft.IdentityModel.", "Microsoft.JSInterop"]),
    ];

    public static TheoryData<string> Assemblies => [.. Rules.Select(r => r.Assembly.GetName().Name!)];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Assembly_references_only_what_its_layer_allows(string assembly)
    {
        var rule = Rules.Single(r => r.Assembly.GetName().Name == assembly);

        var forbidden = rule.Assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(name => !IsBaseLibrary(name))
            .Where(name => name.StartsWith("HomeBuild.", StringComparison.Ordinal)
                ? !rule.HomeBuild.Contains(name)
                : !rule.Packages.Any(p => p == "*" || name.StartsWith(p, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(forbidden.Count == 0,
            $"{assembly} references {string.Join(", ", forbidden)}; allowed: {Describe(rule)} (tests/HomeBuild.Architecture.Tests/ReferenceTests.cs).");
    }

    /// <summary>
    /// The same rule on the project files. GetReferencedAssemblies() lists only assemblies the compiled code uses, so a
    /// project reference added but not used yet would pass the test above.
    /// </summary>
    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Project_file_references_only_allowed_projects(string assembly)
    {
        var rule = Rules.Single(r => r.Assembly.GetName().Name == assembly);
        var project = XDocument.Load(Path.Combine(RepoRoot(), "src", assembly, assembly + ".csproj"));

        var forbidden = project.Descendants("ProjectReference")
            .Where(r => (string?)r.Attribute("ReferenceOutputAssembly") != "false") // analyzers (the generator) are not references
            .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/')))
            .Where(name => !rule.HomeBuild.Contains(name))
            .ToList();

        Assert.True(forbidden.Count == 0, $"{assembly}.csproj references project(s) {string.Join(", ", forbidden)}; allowed: {Describe(rule)}.");
    }

    private static bool IsBaseLibrary(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal) || name is "System" or "netstandard" or "mscorlib";

    private static string Describe(Rule rule) =>
        $"projects [{string.Join(", ", rule.HomeBuild)}], packages [{string.Join(", ", rule.Packages)}]";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir.FullName;
    }
}
