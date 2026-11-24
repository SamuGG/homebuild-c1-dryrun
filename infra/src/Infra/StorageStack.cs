using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace Infra;

/// <summary>
/// C5 Lab 7: floor plans uploaded by browsers straight to S3 with a pre-signed POST the API shapes. The bucket is
/// private; uploads land under incoming/ (deleted after a day if the API never moves them) and the API moves checked
/// files to floor-plans/. The API signs and moves with <see cref="UploadRole"/>, which can do nothing else.
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class StorageStack : Stack
#pragma warning restore CA1711
{
    public StorageStack(Construct scope, string id, IStackProps props, string[] webOrigins, string keysDirectory = "keys", bool snagPhotosCdn = false) : base(scope, id, props)
    {
        // C10 Lab 5's Break only (-c snagPhotosPublic=true): snag photos readable by URL, straight from S3.
        var snagPhotosPublic = Node.TryGetContext("snagPhotosPublic") is "true";
        var bucket = new Bucket(this, "FloorPlans", new BucketProps
        {
            BlockPublicAccess = snagPhotosPublic ? BlockPublicAccess.BLOCK_ACLS_ONLY : BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            ObjectOwnership = ObjectOwnership.BUCKET_OWNER_ENFORCED,
            Cors =
            [
                // Browsers may POST the pre-signed form from the web app only; nothing else is allowed cross-origin.
                new CorsRule
                {
                    AllowedMethods = [HttpMethods.POST],
                    AllowedOrigins = webOrigins,
                    AllowedHeaders = ["*"],
                    MaxAge = 300,
                },
            ],
            LifecycleRules =
            [
                new LifecycleRule
                {
                    Id = "delete-unclaimed-uploads",
                    Prefix = "incoming/",
                    Expiration = Duration.Days(1),
                },
                new LifecycleRule
                {
                    Id = "abort-incomplete-multipart",
                    AbortIncompleteMultipartUploadAfter = Duration.Days(1),
                },
            ],
            // Dev only: cdk destroy empties and deletes the bucket (a custom resource Lambda does the emptying).
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        // The API assumes this role to sign uploads and move checked files. Nothing else: no list, no read outside
        // incoming/, no delete outside incoming/.
        UploadRole = new Role(this, "UploadRole", new RoleProps
        {
            RoleName = "homebuild-api-uploads",
            AssumedBy = new AccountRootPrincipal(),
            Description = "HomeBuild API: sign floor-plan uploads under incoming/, move checked files to floor-plans/, sign diary photo uploads under diary/",
            MaxSessionDuration = Duration.Hours(1),
        });
        UploadRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "SignUploadsAndReadThemForTheMove",
            Actions = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"],
            Resources = [bucket.ArnForObjects("incoming/*")],
        }));
        UploadRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "WriteMovedFloorPlans",
            Actions = ["s3:PutObject"],
            Resources = [bucket.ArnForObjects("floor-plans/*")],
        }));
        UploadRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            // C7 Lab 4: site diary photos are posted straight to diary/{tenant}/{unit}/{guid}.jpg (no move step).
            Sid = "SignDiaryPhotoUploads",
            Actions = ["s3:PutObject"],
            Resources = [bucket.ArnForObjects("diary/*")],
        }));

        UploadRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            // C10 Lab 5: snag photos are posted straight to snags/{tenant}/{snag}/{guid} (images only, 5 MB).
            Sid = "SignSnagPhotoUploads",
            Actions = ["s3:PutObject"],
            Resources = [bucket.ArnForObjects("snags/*")],
        }));

        // C10 Lab 5: snag photos are served through CloudFront only. Origin access control: the bucket policy lets this
        // distribution (and nothing else) read objects; the bucket stays private. The key group means every request needs
        // a URL signed with the private key (in SSM and user-secrets, never in the repository); the public key is here.
        // C11 teardown: the distribution is opt-in (-c snagPhotosCdn=true, or Storage:SnagPhotosCdn in the AppHost), so
        // it exists only while a lab uses it. Without it the API answers 503 for snag photos (CloudFront not configured).
        if (snagPhotosCdn)
        {
            var publicKey = new Amazon.CDK.AWS.CloudFront.PublicKey(this, "SnagPhotosPublicKey2", new Amazon.CDK.AWS.CloudFront.PublicKeyProps
            {
                PublicKeyName = "homebuild-snag-photos-2", // rotated at the C10 teardown: the encoded key cannot change in place, so a new construct ID (and name) creates a new key and the old one is deleted
                Comment = "HomeBuild: verifies signed URLs for snag photos",
                EncodedKey = System.IO.File.ReadAllText(System.IO.Path.Combine(keysDirectory, "snag-photos-public.pem")) /* C11 Lab 5: the AppHost passes infra/keys */,
            });
            var keyGroup = new Amazon.CDK.AWS.CloudFront.KeyGroup(this, "SnagPhotosKeyGroup", new Amazon.CDK.AWS.CloudFront.KeyGroupProps
            {
                KeyGroupName = "homebuild-snag-photos",
                Comment = "HomeBuild: snag photo signers",
                Items = [publicKey],
            });
            Photos = new Amazon.CDK.AWS.CloudFront.Distribution(this, "Photos", new Amazon.CDK.AWS.CloudFront.DistributionProps
            {
                Comment = "homebuild snag photos (signed URLs only)",
                PriceClass = Amazon.CDK.AWS.CloudFront.PriceClass.PRICE_CLASS_100,
                DefaultBehavior = new Amazon.CDK.AWS.CloudFront.BehaviorOptions
                {
                    Origin = Amazon.CDK.AWS.CloudFront.Origins.S3BucketOrigin.WithOriginAccessControl(bucket),
                    ViewerProtocolPolicy = Amazon.CDK.AWS.CloudFront.ViewerProtocolPolicy.HTTPS_ONLY,
                    AllowedMethods = Amazon.CDK.AWS.CloudFront.AllowedMethods.ALLOW_GET_HEAD,
                    CachePolicy = Amazon.CDK.AWS.CloudFront.CachePolicy.CACHING_OPTIMIZED,
                    TrustedKeyGroups = [keyGroup],
                },
            });
            Amazon.CDK.Tags.Of(Photos).Add("lab", "c10-lab5", new TagProps { Priority = 200 }); // above the stack's lab=c5-lab7
            _ = new CfnOutput(this, "PhotosDomain", new CfnOutputProps { Value = Photos.DistributionDomainName });
            _ = new CfnOutput(this, "SnagPhotosKeyId", new CfnOutputProps { Value = publicKey.PublicKeyId });
        }

        if (snagPhotosPublic)
        {
            // The Break: anyone may read snags/* straight from S3, bypassing CloudFront and its signed URLs.
            bucket.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "BreakPublicSnagPhotos",
                Principals = [new AnyPrincipal()],
                Actions = ["s3:GetObject"],
                Resources = [bucket.ArnForObjects("snags/*")],
            }));
        }

        // autoDeleteObjects' provider role and Lambda are raw CfnResources, which Tags.Of does not reach, so the teardown
        // tag query would miss them. Tag them directly with the stack's tags.
        if (Node.TryFindChild("Custom::S3AutoDeleteObjectsCustomResourceProvider") is Construct provider)
        {
            foreach (var resource in ProviderParts.Select(provider.Node.TryFindChild).OfType<CfnResource>())
            {
                resource.AddPropertyOverride("Tags", new[]
                {
                    new Dictionary<string, string> { ["Key"] = "project", ["Value"] = "homebuild" },
                    new Dictionary<string, string> { ["Key"] = "lab", ["Value"] = "c5-lab7" },
                });
            }
        }

        // C9 Lab 3: the provider Lambda first runs at cdk destroy, and Lambda would then create its log group with no
        // retention and leave it behind. Declare that log group here (1 day, deleted with the stack) and make the
        // emptying custom resource depend on it, so CloudFormation deletes the log group only after the Lambda ran.
        if (Node.TryFindChild("Custom::S3AutoDeleteObjectsCustomResourceProvider")?.Node.TryFindChild("Handler") is CfnResource handler
            && bucket.Node.TryFindChild("AutoDeleteObjectsCustomResource") is Construct emptying)
        {
            var providerLogs = new Amazon.CDK.AWS.Logs.LogGroup(this, "AutoDeleteObjectsLogs", new Amazon.CDK.AWS.Logs.LogGroupProps
            {
                LogGroupName = Fn.Join("", ["/aws/lambda/", handler.Ref]),
                Retention = Amazon.CDK.AWS.Logs.RetentionDays.ONE_DAY,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });
            emptying.Node.AddDependency(providerLogs);
        }

        // C9 Lab 3: the contract worker (role from HomeBuild-Messaging, referenced by name so the stacks stay
        // independent) writes contract packs under contracts/, and nothing else in the bucket.
        var contractWorker = Role.FromRoleName(this, "ContractWorker", "homebuild-worker-contracts");
        contractWorker.AttachInlinePolicy(new Policy(this, "ContractWorkerWritesContracts", new PolicyProps
        {
            Statements =
            [
                new PolicyStatement(new PolicyStatementProps
                {
                    Sid = "WriteContractPacks",
                    Actions = ["s3:PutObject"],
                    Resources = [bucket.ArnForObjects("contracts/*")],
                }),
            ],
        }));

        Bucket = bucket;
        _ = new CfnOutput(this, "BucketName", new CfnOutputProps { Value = bucket.BucketName });
        _ = new CfnOutput(this, "UploadRoleArn", new CfnOutputProps { Value = UploadRole.RoleArn });
    }

    private static readonly string[] ProviderParts = ["Role", "Handler"];

    public Role UploadRole { get; }

    /// <summary>C10 Lab 5: the snag photo distribution.</summary>
    public Amazon.CDK.AWS.CloudFront.Distribution? Photos { get; }

    /// <summary>C9 Lab 7: the notifications Lambda writes under notifications/ (a cross-stack reference).</summary>
    public Bucket Bucket { get; }
}
