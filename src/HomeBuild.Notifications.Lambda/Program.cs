using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using HomeBuild.Notifications.Lambda;

// C9 Lab 7: the custom runtime's entry point. Lambda runs this executable (bootstrap); LambdaBootstrap polls the
// Runtime API for events and calls the handler. Created once per execution environment, reused by warm invocations.
//   NOTIFICATIONS_BUCKET     the storage bucket (notifications/ prefix)
//   PARTIAL_BATCH_RESPONSES  "false" only for the Break (with ReportBatchItemFailures off on the event source mapping)
var bucket = Environment.GetEnvironmentVariable("NOTIFICATIONS_BUCKET") ?? throw new InvalidOperationException("NOTIFICATIONS_BUCKET is not set.");
var partial = Environment.GetEnvironmentVariable("PARTIAL_BATCH_RESPONSES") != "false";
using var s3 = new AmazonS3Client();
var function = new NotificationsFunction(new S3NotificationStore(s3, bucket), TimeProvider.System, partial);
await LambdaBootstrapBuilder.Create<SQSEvent, SQSBatchResponse>(function.HandleAsync, new SourceGeneratorLambdaJsonSerializer<LambdaJson>())
    .Build()
    .RunAsync();
