using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.SimpleEmailV2;
using HomeBuild.Notifications.Lambda;

// C9 Lab 7: the custom runtime's entry point. Lambda runs this executable (bootstrap); LambdaBootstrap polls the
// Runtime API for events and calls the handler. Created once per execution environment, reused by warm invocations.
//   NOTIFICATIONS_BUCKET     the storage bucket (notifications/ prefix)
//   PARTIAL_BATCH_RESPONSES  "false" only for the Break (with ReportBatchItemFailures off on the event source mapping)
//   EMAIL_FROM, AFTERCARE_EMAIL, BUYER_EMAIL  C10 Lab 6: SES sender and recipients (verified addresses in the sandbox)
var bucket = Environment.GetEnvironmentVariable("NOTIFICATIONS_BUCKET") ?? throw new InvalidOperationException("NOTIFICATIONS_BUCKET is not set.");
var partial = Environment.GetEnvironmentVariable("PARTIAL_BATCH_RESPONSES") != "false";
using var s3 = new AmazonS3Client();
using var ses = new AmazonSimpleEmailServiceV2Client();
var from = Environment.GetEnvironmentVariable("EMAIL_FROM");
var function = new NotificationsFunction(new S3NotificationStore(s3, bucket), TimeProvider.System, partial,
    from is { Length: > 0 } ? new SesEmailSender(ses, from) : null,
    new EmailRouting(Environment.GetEnvironmentVariable("AFTERCARE_EMAIL"), Environment.GetEnvironmentVariable("BUYER_EMAIL")));
await LambdaBootstrapBuilder.Create<SQSEvent, SQSBatchResponse>(function.HandleAsync, new SourceGeneratorLambdaJsonSerializer<LambdaJson>())
    .Build()
    .RunAsync();
