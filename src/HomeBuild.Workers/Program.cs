using Amazon;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using HomeBuild.Messaging;
using HomeBuild.Workers;
using Microsoft.Extensions.Options;

// C9 Lab 3: the worker host. Run locally with `dotnet run --project src/HomeBuild.Workers` (Development: the API's
// user-secrets give the connection string; AWS_PROFILE gives credentials). Settings:
//   Workers:Contracts:QueueUrl   the homebuild-contracts queue (the consumer runs only when it is set)
//   Storage:BucketName           the bucket contract packs go to (contracts/)
//   Messaging:TopicArn           homebuild-events, for ContractIssued (the relay runs only when it is set)
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWorkers(builder.Configuration);
builder.Build().Run();

namespace HomeBuild.Workers
{
    public static class WorkerServices
    {
        public static IServiceCollection AddWorkers(this IServiceCollection services, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var connection = configuration.GetConnectionString("HomeBuild") ?? throw new InvalidOperationException("Connection string 'HomeBuild' is missing.");
            services.AddSingleton(TimeProvider.System);
            // Scoped: one per message (each processor takes its own scope, so its own connection).
            services.AddDbContext<ContractsDbContext>(options => ContractsDbContext.Configure(options, connection));
            services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section);
            services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.Section);
            services.AddOptions<ConsumerOptions>().BindConfiguration("Workers:Contracts");

            // Singletons: the SDK clients are thread-safe; credentials from the default chain.
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(RegionEndpoint.EUWest2));
            services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(RegionEndpoint.EUWest2));
            services.AddSingleton<IAmazonSimpleNotificationService>(_ => new AmazonSimpleNotificationServiceClient(RegionEndpoint.EUWest2));
            services.AddScoped<IContractDocuments, S3ContractDocuments>();
            services.AddScoped<IEventPublisher, SnsEventPublisher>();
            services.AddKeyedScoped<IMessageHandler, ContractWorker>(ContractWorker.Name);

            // ContractIssued leaves through the contracts outbox.
            services.AddSingleton<OutboxRelay<ContractsDbContext>>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay<ContractsDbContext>>());

            if (configuration["Workers:Contracts:QueueUrl"] is { Length: > 0 } queueUrl)
            {
                services.AddSingleton<IQueueClient>(sp => new SqsQueueClient(sp.GetRequiredService<IAmazonSQS>(), queueUrl));
                services.AddSingleton(sp => new QueueConsumer(ContractWorker.Name, sp.GetRequiredService<IQueueClient>(), sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IOptions<ConsumerOptions>>().Value, sp.GetRequiredService<ILogger<QueueConsumer>>()));
                services.AddHostedService(sp => sp.GetRequiredService<QueueConsumer>());
            }

            return services;
        }
    }
}
