using System.Reflection;
using System.Text.RegularExpressions;

namespace HomeBuild.Api.Tests;

public sealed partial class ThreatModelTests
{
    [Fact]
    public void Every_stride_row_has_a_mitigation_and_a_test_or_reasoned_acceptance()
    {
        var model = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "security", "threat-model.md"));
        var rows = model.SkipWhile(l => l != "## STRIDE").Skip(1)
            .TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal))
            .Where(l => StrideRow().IsMatch(l))
            .Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        var testNames = typeof(ThreatModelTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(rows.Count >= 6, $"{rows.Count} STRIDE rows");
        foreach (var r in rows)
        {
            var (id, mitigation, proof) = (r[1], r[4], r[5]);
            Assert.False(string.IsNullOrWhiteSpace(mitigation), $"{id}: no mitigation");
            Assert.False(string.IsNullOrWhiteSpace(proof), $"{id}: no test or acceptance");
            var named = TestName().Matches(proof).Select(m => m.Groups[1].Value).ToList();
            Assert.True(named.Count > 0 || proof.Contains("red-team", StringComparison.Ordinal) || proof.StartsWith("Accepted", StringComparison.Ordinal),
                $"{id}: proof names no test, red-team request or reasoned acceptance: {proof}");
            Assert.All(named, n => Assert.True(testNames.Contains(n), $"{id}: test {n} does not exist"));
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("HomeBuild.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^\| [STRIDE]\d+ \|")]
    private static partial Regex StrideRow();

    // Test method names in backticks, e.g. `Expired_token_returns_401`.
    [GeneratedRegex(@"`([A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+)`")]
    private static partial Regex TestName();
}
