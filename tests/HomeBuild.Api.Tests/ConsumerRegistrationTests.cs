using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Api.Scheduling;
using HomeBuild.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 7: the API hosts three queue consumers (extras, change orders, scheduled work). Each is a QueueConsumer, and
/// AddHostedService(factory) adds through TryAddEnumerable, which keeps one IHostedService per implementation type: with
/// every queue configured only the first consumer ran. Found by the Lab 7 journey (ExtrasPaid waited in
/// homebuild-change-orders with no receiver).
/// </summary>
public sealed class ConsumerRegistrationTests
{
    [Fact]
    public void Every_configured_queue_gets_its_own_consumer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Extras:QueueUrl"] = "https://sqs.eu-west-2.amazonaws.com/000000000000/homebuild-extras",
            ["ChangeOrders:QueueUrl"] = "https://sqs.eu-west-2.amazonaws.com/000000000000/homebuild-change-orders",
            ["Scheduling:QueueUrl"] = "https://sqs.eu-west-2.amazonaws.com/000000000000/homebuild-scheduled",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IServiceScopeFactory>(_ => null!);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddExtrasConsumer(configuration);
        ScheduledWorkServices.AddScheduledWork(services, configuration);
        ChangeOrderServices.AddChangeOrderConsumer(services, configuration);

        using var provider = services.BuildServiceProvider();
        var consumers = provider.GetServices<IHostedService>().OfType<QueueConsumer>().Select(c => c.Name).Order().ToList();
        Assert.Equal([ChangeOrdersConsumer.Name, ExtrasProgressConsumer.Name, ScheduledWorkHandler.Name], consumers);
    }
}
