using Xunit.Sdk;
using Xunit.v3;

[assembly: TestClassOrderer(typeof(HomeBuild.Api.Tests.RandomTestOrder))]
[assembly: TestMethodOrderer(typeof(HomeBuild.Api.Tests.RandomTestOrder))]
[assembly: TestCaseOrderer(typeof(HomeBuild.Api.Tests.RandomTestOrder))]

namespace HomeBuild.Api.Tests;

/// <summary>
/// Runs the tests of each collection in a shuffled order, a different one each run (C6 Lab 3). xUnit v3's own order is
/// unpredictable but stable for a given assembly ("stable randomization" from the assembly's unique ID), so it would
/// never find a test that depends on another running first. Set HOMEBUILD_TEST_SEED to replay an order; CI sets a new
/// seed per run and prints it in the job summary.
/// <para>xUnit 4 orders in layers (collection, then class, then method, then case), and a case orderer only sees the
/// cases of one method. So the same seeded shuffle is registered for classes and methods too; with the case orderer
/// alone, most classes ran in the same order whatever the seed (C8 Lab 0).</para>
/// </summary>
public sealed class RandomTestOrder : ITestClassOrderer, ITestMethodOrderer, ITestCaseOrderer
{
    public const string SeedVariable = "HOMEBUILD_TEST_SEED";

    public static int Seed { get; } = int.TryParse(Environment.GetEnvironmentVariable(SeedVariable), out var seed)
        ? seed
        : Random.Shared.Next();

    // xUnit 4 passes null for test cases that have no class or method (custom test cases); they sort first.
    public IReadOnlyCollection<TTestClass?> OrderTestClasses<TTestClass>(IReadOnlyCollection<TTestClass?> testClasses)
        where TTestClass : ITestClass =>
        Shuffle(testClasses, c => c?.TestClassName ?? "");

    public IReadOnlyCollection<TTestMethod?> OrderTestMethods<TTestMethod>(IReadOnlyCollection<TTestMethod?> testMethods)
        where TTestMethod : ITestMethod =>
        Shuffle(testMethods, m => m is null ? "" : m.TestClass.TestClassName + "." + m.MethodName);

    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase =>
        Shuffle(testCases, c => c.TestCaseDisplayName);

    // Sort first, so the same seed gives the same order whatever order the items arrive in.
    private static T[] Shuffle<T>(IReadOnlyCollection<T> items, Func<T, string> key)
    {
        var sorted = items.OrderBy(key, StringComparer.Ordinal).ToArray();
        new Random(Seed).Shuffle(sorted);
        return sorted;
    }
}
