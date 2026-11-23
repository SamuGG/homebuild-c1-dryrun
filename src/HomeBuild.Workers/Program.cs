using Amazon;
using Microsoft.AspNetCore.Builder;
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
//   Workers:Contracts:DeadLetterQueueUrl  homebuild-contracts-dlq, whose depth is logged every DeadLetterCheck (Lab 4)
//   Workers:Notifications:QueueUrl        homebuild-notifications (Lab 5; the consumer runs only when it is set)
// C11 Lab 1: a slim web host so the container answers /alive (liveness only); the workers are unchanged.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddWorkers(builder.Configuration);
builder.AddServiceDefaults(); // C11 Lab 4: OpenTelemetry and health checks
var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

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
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, connection));
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
            services.AddKeyedScoped<IMessageHandler, NotificationWorker>(NotificationWorker.Name);

            // ContractIssued leaves through the contracts outbox.
            services.AddSingleton<OutboxRelay<ContractsDbContext>>();
            services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay<ContractsDbContext>>());

            if (configuration["Workers:Contracts:QueueUrl"] is { Length: > 0 } queueUrl)
            {
                services.AddSingleton<IQueueClient>(sp => new SqsQueueClient(sp.GetRequiredService<IAmazonSQS>(), queueUrl));
                services.AddSingleton(sp => new QueueConsumer(ContractWorker.Name, sp.GetRequiredService<IQueueClient>(), sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IOptions<ConsumerOptions>>().Value, sp.GetRequiredService<ILogger<QueueConsumer>>()));
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<QueueConsumer>()); // not AddHostedService: see the notifications consumer below
            }

            if (configuration["Workers:Notifications:QueueUrl"] is { Length: > 0 } notificationsUrl)
            {
                var options = configuration.GetSection("Workers:Notifications").Get<ConsumerOptions>() ?? new ConsumerOptions();
                // AddSingleton, not AddHostedService: that adds through TryAddEnumerable, one per implementation type, so a second QueueConsumer in the host was dropped (C10 Lab 7).
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new QueueConsumer(NotificationWorker.Name, new SqsQueueClient(sp.GetRequiredService<IAmazonSQS>(), notificationsUrl),
                    sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetRequiredService<ILogger<QueueConsumer>>()));
            }

            if (configuration["Workers:Contracts:DeadLetterQueueUrl"] is { Length: > 0 } dlqUrl)
            {
                var interval = configuration.GetValue("Workers:Contracts:DeadLetterCheck", TimeSpan.FromMinutes(5));
                services.AddHostedService(sp => new DeadLetterMonitor("homebuild-contracts-dlq", new SqsQueueClient(sp.GetRequiredService<IAmazonSQS>(), dlqUrl),
                    interval, sp.GetRequiredService<ILogger<DeadLetterMonitor>>(), sp.GetRequiredService<TimeProvider>()));
            }

            return services;
        }
    }
}
