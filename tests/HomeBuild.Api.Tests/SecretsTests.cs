using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBuild.Api.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Tests;

public sealed partial class SecretsTests
{
    [Fact]
    public void No_secrets_in_appsettings()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tools"), "appsettings*.json", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var hits = new List<string>();
        foreach (var file in files)
        {
            foreach (var (key, value) in Leaves(JsonElement.Parse(File.ReadAllText(file)), ""))
            {
                if ((SecretKey().IsMatch(key) && value.Length > 0) || SecretValue().IsMatch(value))
                {
                    hits.Add($"{Path.GetRelativePath(root, file)}: {key}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Secrets belong in user-secrets (local) or SSM (AWS):\n" + string.Join("\n", hits));
    }

    [Fact]
    public async Task Payments_secret_key_is_redacted_in_startup_log()
    {
        const string secret = "sk_test_this-must-not-be-logged";
        var logs = new FakeLogCollector();
        var logger = new FakeLogger<ConfigurationStartupLogger>(logs);
        var options = Options.Create(new PaymentsOptions { SecretKey = secret, PublishableKey = "pk_test_visible" });

        await new ConfigurationStartupLogger(options, logger).StartAsync(TestContext.Current.CancellationToken);

        var record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(1101, record.Id.Id);
        Assert.DoesNotContain(secret, record.Message, StringComparison.Ordinal);
        Assert.Contains("SecretKey=***", record.Message, StringComparison.Ordinal);
        Assert.Contains("PublishableKey=pk_test_visible", record.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the real /homebuild/staging/ parameters, so it needs AWS credentials. Opt-in: CI has no AWS credentials,
    /// and on a developer machine the SDK may find some anyway. Run with
    /// HOMEBUILD_AWS_TESTS=1 AWS_PROFILE=... AWS_REGION=eu-west-2 dotnet test.
    /// </summary>
    [Fact]
    public void Ssm_parameters_load_in_staging_configuration()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1",
            "Needs AWS: set HOMEBUILD_AWS_TESTS=1 with AWS_PROFILE and AWS_REGION=eu-west-2.");

        using var staging = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
            b.UseSetting("ConnectionStrings:HomeBuild", "Host=unused");
        });

        var configuration = staging.Services.GetRequiredService<IConfiguration>();
        var payments = staging.Services.GetRequiredService<IOptions<PaymentsOptions>>().Value;

        Assert.Equal("/homebuild/staging/", ConfigurationSources.SsmPath("Staging"));
        Assert.Equal("homebuild-staging-client", configuration["Tokens:AllowedClientIds:0"]); // String parameter
        Assert.False(string.IsNullOrEmpty(payments.SecretKey)); // SecureString parameter, decrypted by the provider
        Assert.StartsWith("dryrun-", payments.SecretKey, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Key, string Value)> Leaves(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    foreach (var leaf in Leaves(p.Value, path.Length == 0 ? p.Name : $"{path}:{p.Name}"))
                    {
                        yield return leaf;
                    }
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var leaf in Leaves(item, $"{path}:{i++}"))
                    {
                        yield return leaf;
                    }
                }

                break;
            default:
                yield return (path, element.ToString());
                break;
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("HomeBuild.slnx not found");
    }

    // Keys whose values are secrets.
    [GeneratedRegex(@"(?i)(password|passwd|secret|api[_-]?key|private[_-]?key|access[_-]?key|client[_-]?secret|signingkeys)")]
    private static partial Regex SecretKey();

    // Values that look like secrets wherever they appear.
    [GeneratedRegex(@"(?i)(password=|pwd=|sk_(live|test)_|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|xox[abp]-|ghp_[A-Za-z0-9]{20,})")]
    private static partial Regex SecretValue();
}
