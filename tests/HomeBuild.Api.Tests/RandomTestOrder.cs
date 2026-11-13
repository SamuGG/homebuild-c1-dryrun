using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCaseOrderer(typeof(HomeBuild.Api.Tests.RandomTestOrder))]

namespace HomeBuild.Api.Tests;

/// <summary>
/// Runs the test cases of each collection in a shuffled order, a different one each run (C6 Lab 3). xUnit v3's own
/// order is unpredictable but stable for a given assembly ("stable randomization" from the assembly's unique ID), so it
/// would never find a test that depends on another running first. Set HOMEBUILD_TEST_SEED to replay an order; CI sets
/// a new seed per run and prints it in the job summary.
/// </summary>
public sealed class RandomTestOrder : ITestCaseOrderer
{
    public const string SeedVariable = "HOMEBUILD_TEST_SEED";

    public static int Seed { get; } = int.TryParse(Environment.GetEnvironmentVariable(SeedVariable), out var seed)
        ? seed
        : Random.Shared.Next();

    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase
    {
        // Sort first, so the same seed gives the same order whatever order the cases arrive in.
        var cases = testCases.OrderBy(c => c.TestCaseDisplayName, StringComparer.Ordinal).ToArray();
        new Random(Seed).Shuffle(cases);
        return cases;
    }
}
