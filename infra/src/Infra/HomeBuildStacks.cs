using Amazon.CDK;
using Constructs;

namespace Infra;

/// <summary>
/// C11 Lab 5: how HomeBuild's shared stacks are built, in one place, so the CDK app (Program.cs) and the Aspire AppHost
/// synthesize the same templates: same stack names, construct IDs, tags and feature flags. Anything different would be
/// an update to the live stacks.
/// </summary>
public static class HomeBuildStacks
{
    public const string Region = "eu-west-2";

    /// <summary>The app-wide tags (and, for an app the AppHost created, cdk.json's feature flags); call once per app.</summary>
    public static void Prepare(App app, string? cdkJsonPath = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Node.TryGetContext("homebuild:prepared") is "true")
        {
            return;
        }

        if (cdkJsonPath is not null)
        {
            // The AppHost's App does not read cdk.json: copy its context (feature flags) before any stack is added.
            using var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(cdkJsonPath));
            if (json.RootElement.TryGetProperty("context", out var context))
            {
                foreach (var flag in context.EnumerateObject())
                {
                    app.Node.SetContext(flag.Name, flag.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        System.Text.Json.JsonValueKind.Array => flag.Value.EnumerateArray().Select(v => v.GetString()).ToArray(),
                        _ => flag.Value.ToString(),
                    });
                }
            }
        }

        app.Node.SetContext("homebuild:prepared", "true");
        Tags.Of(app).Add("project", "homebuild");
        Tags.Of(app).Add("lab", app.Node.TryGetContext("lab") as string ?? "c4");
    }

    public static Amazon.CDK.Environment Env(string? account) => new() { Account = account, Region = Region };

    public static IdentityStack Identity(Construct scope, Amazon.CDK.Environment env)
    {
        var stack = new IdentityStack(scope, "HomeBuild-Identity", new StackProps { Env = env },
            domainPrefix: scope.Node.TryGetContext("domainPrefix") as string ?? "homebuild-dryrun-dev");
        Tags.Of(stack).Add("lab", "c4-lab2");
        return stack;
    }

    public static StorageStack Storage(Construct scope, Amazon.CDK.Environment env, string keysDirectory = "keys", bool? snagPhotosCdn = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stack = new StorageStack(scope, "HomeBuild-Storage", new StackProps { Env = env }, webOrigins: ["http://localhost:5266"], keysDirectory,
            snagPhotosCdn ?? scope.Node.TryGetContext("snagPhotosCdn") is "true" or true);
        Tags.Of(stack).Add("lab", "c5-lab7");
        return stack;
    }

    /// <summary>
    /// The two outputs HomeBuild-Notifications reads through Fn::GetStackOutput (C10). CDK generates them only when the
    /// Notifications stack is in the same app; without it, a Storage deploy deleted them and the next Notifications
    /// deploy failed (C10 Lab 6). Apps without that stack (the AppHost, a plain cdk deploy) declare them here, with the
    /// same logical IDs and values, so the live Storage stack keeps them.
    /// </summary>
    public static void KeepNotificationsOutputs(StorageStack storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        new CfnOutput(storage, "KeepPublishBucketArn", new CfnOutputProps { Value = storage.Bucket.BucketArn })
            .OverrideLogicalId("PublishOutputFnGetAttFloorPlansC82CA112Arn834C32C4");
        new CfnOutput(storage, "KeepPublishBucketName", new CfnOutputProps { Value = storage.Bucket.BucketName })
            .OverrideLogicalId("PublishOutputRefFloorPlansC82CA11292AB383D");
    }

    public static MessagingStack Messaging(Construct scope, Amazon.CDK.Environment env)
    {
        var stack = new MessagingStack(scope, "HomeBuild-Messaging", new StackProps { Env = env });
        Tags.Of(stack).Add("lab", "c9");
        return stack;
    }
}
