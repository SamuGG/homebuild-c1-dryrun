using Amazon.CDK;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace Infra;

/// <summary>
/// Lab 1's only resource: a free standard SSM parameter, so `cdk synth` has something to show the tags on.
/// Later labs add their own stacks (Lab 2: Identity).
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class FoundationStack : Stack
#pragma warning restore CA1711
{
    public FoundationStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        _ = new StringParameter(this, "Environment", new StringParameterProps
        {
            ParameterName = "/homebuild/dev/environment",
            StringValue = "dev",
            Description = "Marks the HomeBuild dev environment (free standard parameter).",
        });
    }
}
