using System.Text.Json.Nodes;
using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var serviceBus = builder.AddAzureServiceBus("sbemulator");

var queue = serviceBus.AddServiceBusQueue("queueOne", "queue1")
    .WithProperties(queue => queue.DeadLetteringOnMessageExpiration = false);

var subscription = serviceBus.AddServiceBusTopic("topicOne", "topic1")
    .AddServiceBusSubscription("sub1")
    .WithProperties(subscription =>
    {
        subscription.MaxDeliveryCount = 10;

        var rule = new AzureServiceBusRule("app-prop-filter-1")
        {
            CorrelationFilter = new()
            {
                ContentType = "application/text",
                CorrelationId = "id1",
                Subject = "subject1",
                MessageId = "msgid1",
                ReplyTo = "someQueue",
                ReplyToSessionId = "sessionId",
                SessionId = "session1",
                SendTo = "xyz"
            }
        };
        subscription.Rules.Add(rule);
    });

serviceBus.RunAsEmulator(configure => configure.WithConfiguration(document =>
{
    document["UserConfig"]!["Logging"] = new JsonObject { ["Type"] = "Console" };
}).WithLifetime(ContainerLifetime.Persistent));

builder.AddProject<Projects.ServiceBusWorker>("worker")
    .WithReference(queue).WaitFor(queue)
    .WithReference(subscription).WaitFor(subscription);

builder.Build().Run();
