// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.ServiceBus;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureServiceBusExtensionsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ResourceNamesCanBeDifferentThanAzureNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("sb");

        serviceBus.AddServiceBusQueue("queue1", "queueName")
            .WithProperties(queue => queue.DefaultMessageTimeToLive = TimeSpan.FromSeconds(1));
        var topic1 = serviceBus.AddServiceBusTopic("topic1", "topicName")
            .WithProperties(topic =>
            {
                topic.DefaultMessageTimeToLive = TimeSpan.FromSeconds(1);
            });
        topic1.AddServiceBusSubscription("subscription1", "subscriptionName")
            .WithProperties(sub =>
            {
                sub.Rules.Add(new AzureServiceBusRule("rule1"));
            });

        var manifest = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TopicNamesCanBeLongerThan24(bool useObsoleteMethods)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("sb");

        if (useObsoleteMethods)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            serviceBus.AddTopic("device-connection-state-events1234567890-even-longer");
#pragma warning restore CS0618 // Type or member is obsolete
        }
        else
        {
            serviceBus.AddServiceBusTopic("device-connection-state-events1234567890-even-longer");
        }

        var manifest = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact(Skip = "Azure ServiceBus emulator is not reliable in CI - https://github.com/microsoft/aspire/issues/7066")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnServiceBusEmulatorBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddAzureServiceBus("resource")
                              .RunAsEmulator()
                              .WithHealthCheck("blocking_check");

        resource.AddServiceBusQueue("queue1");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Theory(Skip = "Azure ServiceBus emulator is not reliable in CI - https://github.com/microsoft/aspire/issues/7066")]
    [InlineData(null)]
    [InlineData("other")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureServiceBusEmulatorResource(string? queueName)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(output);

        var serviceBus = builder.AddAzureServiceBus("servicebusns")
            .RunAsEmulator();

        var queueResource = serviceBus.AddServiceBusQueue("queue123", queueName);

        using var app = builder.Build();
        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.Configuration["ConnectionStrings:servicebusns"] = await serviceBus.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.AddAzureServiceBusClient("servicebusns");

        using var host = hb.Build();
        await host.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceAsync(serviceBus.Resource.Name, KnownResourceStates.Running, cts.Token);
        await rns.WaitForResourceHealthyAsync(serviceBus.Resource.Name, cts.Token);

        var serviceBusClient = host.Services.GetRequiredService<ServiceBusClient>();

        await using var sender = serviceBusClient.CreateSender(queueResource.Resource.QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage("Hello, World!"), cts.Token);

        await using var receiver = serviceBusClient.CreateReceiver(queueResource.Resource.QueueName);
        var message = await receiver.ReceiveMessageAsync(cancellationToken: cts.Token);

        Assert.Equal("Hello, World!", message.Body.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8081)]
    [InlineData(9007)]
    public void AddAzureServiceBusWithEmulatorGetsExpectedPort(int? port = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("sb").RunAsEmulator(configureContainer: builder =>
        {
            builder.WithHostPort(port);
        });

        Assert.Collection(
            serviceBus.Resource.Annotations.OfType<EndpointAnnotation>(),
            e => Assert.Equal(port, e.Port),
            e => Assert.Equal(5300, e.TargetPort)
            );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.3.97-preview")]
    [InlineData("1.0.7")]
    public void AddAzureServiceBusWithEmulatorGetsExpectedImageTag(string? imageTag)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("sb");

        serviceBus.RunAsEmulator(container =>
        {
            if (!string.IsNullOrEmpty(imageTag))
            {
                container.WithImageTag(imageTag);
            }
        });

        var containerImageAnnotation = serviceBus.Resource.Annotations.OfType<ContainerImageAnnotation>().FirstOrDefault();
        Assert.NotNull(containerImageAnnotation);

        Assert.Equal(imageTag ?? ServiceBusEmulatorContainerImageTags.Tag, containerImageAnnotation.Tag);
        Assert.Equal(ServiceBusEmulatorContainerImageTags.Registry, containerImageAnnotation.Registry);
        Assert.Equal(ServiceBusEmulatorContainerImageTags.Image, containerImageAnnotation.Image);
    }

    [Fact]
    public async Task AzureServiceBusEmulatorResourceInitializesProvisioningModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        global::Azure.Provisioning.ServiceBus.ServiceBusQueue? queue = null;
        global::Azure.Provisioning.ServiceBus.ServiceBusTopic? topic = null;
        global::Azure.Provisioning.ServiceBus.ServiceBusSubscription? subscription = null;
        global::Azure.Provisioning.ServiceBus.ServiceBusRule? rule = null;

        var serviceBus = builder.AddAzureServiceBus("servicebusns");
        serviceBus.AddServiceBusQueue("queue1")
            .WithProperties(queue =>
            {
                queue.DeadLetteringOnMessageExpiration = true;
                queue.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                queue.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20);
                queue.ForwardDeadLetteredMessagesTo = "someQueue";
                queue.LockDuration = TimeSpan.FromMinutes(5);
                queue.MaxDeliveryCount = 10;
                queue.RequiresDuplicateDetection = true;
                queue.RequiresSession = true;
            });

        var topic1 = serviceBus.AddServiceBusTopic("topic1")
            .WithProperties(topic =>
            {
                topic.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                topic.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20);
                topic.RequiresDuplicateDetection = true;
            });
        topic1.AddServiceBusSubscription("subscription1")
            .WithProperties(sub =>
            {
                sub.DeadLetteringOnMessageExpiration = true;
                sub.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                sub.LockDuration = TimeSpan.FromMinutes(5);
                sub.MaxDeliveryCount = 10;
                sub.ForwardDeadLetteredMessagesTo = "";
                sub.RequiresSession = true;

                var rule = new AzureServiceBusRule("rule1")
                {
                    FilterType = AzureServiceBusFilterType.SqlFilter,
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
                sub.Rules.Add(rule);
            });

        serviceBus
            .ConfigureInfrastructure(infrastructure =>
            {
                queue = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.ServiceBus.ServiceBusQueue>().Single();
                topic = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.ServiceBus.ServiceBusTopic>().Single();
                subscription = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.ServiceBus.ServiceBusSubscription>().Single();
                rule = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.ServiceBus.ServiceBusRule>().Single();
            });

        using var app = builder.Build();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(serviceBus.Resource);

        Assert.NotNull(queue);
        Assert.Equal("queue1", queue.Name.Value);
        Assert.True(queue.DeadLetteringOnMessageExpiration.Value);
        Assert.Equal(TimeSpan.FromMinutes(1), queue.DefaultMessageTimeToLive.Value);
        Assert.Equal(TimeSpan.FromSeconds(20), queue.DuplicateDetectionHistoryTimeWindow.Value);
        Assert.Equal("someQueue", queue.ForwardDeadLetteredMessagesTo.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), queue.LockDuration.Value);
        Assert.Equal(10, queue.MaxDeliveryCount.Value);
        Assert.True(queue.RequiresDuplicateDetection.Value);
        Assert.True(queue.RequiresSession.Value);

        Assert.NotNull(topic);
        Assert.Equal("topic1", topic.Name.Value);
        Assert.Equal(TimeSpan.FromMinutes(1), topic.DefaultMessageTimeToLive.Value);
        Assert.Equal(TimeSpan.FromSeconds(20), topic.DuplicateDetectionHistoryTimeWindow.Value);
        Assert.True(topic.RequiresDuplicateDetection.Value);

        Assert.NotNull(subscription);
        Assert.Equal("subscription1", subscription.Name.Value);
        Assert.True(subscription.DeadLetteringOnMessageExpiration.Value);
        Assert.Equal(TimeSpan.FromMinutes(1), subscription.DefaultMessageTimeToLive.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), subscription.LockDuration.Value);
        Assert.Equal(10, subscription.MaxDeliveryCount.Value);
        Assert.Equal("", subscription.ForwardDeadLetteredMessagesTo.Value);
        Assert.True(subscription.RequiresSession.Value);

        Assert.NotNull(rule);
        Assert.Equal("rule1", rule.Name.Value);
        Assert.Equal(global::Azure.Provisioning.ServiceBus.ServiceBusFilterType.SqlFilter, rule.FilterType.Value);
        Assert.Equal("application/text", rule.CorrelationFilter.ContentType.Value);
        Assert.Equal("id1", rule.CorrelationFilter.CorrelationId.Value);
        Assert.Equal("subject1", rule.CorrelationFilter.Subject.Value);
        Assert.Equal("msgid1", rule.CorrelationFilter.MessageId.Value);
        Assert.Equal("someQueue", rule.CorrelationFilter.ReplyTo.Value);
        Assert.Equal("sessionId", rule.CorrelationFilter.ReplyToSessionId.Value);
        Assert.Equal("session1", rule.CorrelationFilter.SessionId.Value);
        Assert.Equal("xyz", rule.CorrelationFilter.SendTo.Value);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureServiceBusEmulatorResourceGeneratesConfigJson()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("servicebusns")
            .RunAsEmulator();
        serviceBus.AddServiceBusQueue("queue1")
            .WithProperties(queue =>
            {
                queue.DeadLetteringOnMessageExpiration = true;
                queue.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                queue.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20);
                queue.ForwardDeadLetteredMessagesTo = "someQueue";
                queue.LockDuration = TimeSpan.FromMinutes(5);
                queue.MaxDeliveryCount = 10;
                queue.RequiresDuplicateDetection = true;
                queue.RequiresSession = true;
            });

        var topic1 = serviceBus.AddServiceBusTopic("topic1")
            .WithProperties(topic =>
            {
                topic.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                topic.DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20);
                topic.RequiresDuplicateDetection = true;
            });
        topic1.AddServiceBusSubscription("subscription1")
            .WithProperties(sub =>
            {
                sub.DeadLetteringOnMessageExpiration = true;
                sub.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
                sub.LockDuration = TimeSpan.FromMinutes(5);
                sub.MaxDeliveryCount = 10;
                sub.ForwardDeadLetteredMessagesTo = "";
                sub.RequiresSession = true;

                var rule = new AzureServiceBusRule("rule1")
                {
                    FilterType = AzureServiceBusFilterType.SqlFilter,
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
                sub.Rules.Add(rule);
            });

        using var app = builder.Build();
        await app.StartAsync();

        var serviceBusEmulatorResource = builder.Resources.OfType<AzureServiceBusResource>().Single(x => x is { } serviceBusResource && serviceBusResource.IsEmulator);
        var configAnnotation = serviceBusEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/ServiceBus_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = serviceBusEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal(/*json*/"""
        {
          "UserConfig": {
            "Namespaces": [
              {
                "Name": "servicebusns",
                "Queues": [
                  {
                    "Name": "queue1",
                    "Properties": {
                      "DeadLetteringOnMessageExpiration": true,
                      "DefaultMessageTimeToLive": "PT1M",
                      "DuplicateDetectionHistoryTimeWindow": "PT20S",
                      "ForwardDeadLetteredMessagesTo": "someQueue",
                      "LockDuration": "PT5M",
                      "MaxDeliveryCount": 10,
                      "RequiresDuplicateDetection": true,
                      "RequiresSession": true
                    }
                  }
                ],
                "Topics": [
                  {
                    "Name": "topic1",
                    "Properties": {
                      "DefaultMessageTimeToLive": "PT1M",
                      "DuplicateDetectionHistoryTimeWindow": "PT20S",
                      "RequiresDuplicateDetection": true
                    },
                    "Subscriptions": [
                      {
                        "Name": "subscription1",
                        "Properties": {
                          "DeadLetteringOnMessageExpiration": true,
                          "DefaultMessageTimeToLive": "PT1M",
                          "ForwardDeadLetteredMessagesTo": "",
                          "LockDuration": "PT5M",
                          "MaxDeliveryCount": 10,
                          "RequiresSession": true
                        },
                        "Rules": [
                          {
                            "Name": "rule1",
                            "Properties": {
                              "FilterType": "Sql",
                              "CorrelationFilter": {
                                "CorrelationId": "id1",
                                "MessageId": "msgid1",
                                "To": "xyz",
                                "ReplyTo": "someQueue",
                                "Label": "subject1",
                                "SessionId": "session1",
                                "ReplyToSessionId": "sessionId",
                                "ContentType": "application/text"
                              }
                            }
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            ],
            "Logging": {
              "Type": "File"
            }
          }
        }
        """, configFile.Contents);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureServiceBusEmulatorResourceGeneratesConfigJsonOnlyChangedProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("servicebusns")
            .RunAsEmulator();
        serviceBus.AddServiceBusQueue("queue1")
            .WithProperties(queue =>
            {
                queue.DefaultMessageTimeToLive = TimeSpan.FromMinutes(1);
            });

        using var app = builder.Build();
        await app.StartAsync();

        var serviceBusEmulatorResource = builder.Resources.OfType<AzureServiceBusResource>().Single(x => x is { } serviceBusResource && serviceBusResource.IsEmulator);
        var configAnnotation = serviceBusEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/ServiceBus_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = serviceBusEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal("""
            {
              "UserConfig": {
                "Namespaces": [
                  {
                    "Name": "servicebusns",
                    "Queues": [
                      {
                        "Name": "queue1",
                        "Properties": {
                          "DefaultMessageTimeToLive": "PT1M"
                        }
                      }
                    ],
                    "Topics": []
                  }
                ],
                "Logging": {
                  "Type": "File"
                }
              }
            }
            """, configFile.Contents);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureServiceBusEmulatorResourceGeneratesConfigJsonWithCustomizations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("servicebusns")
            .RunAsEmulator(configure => configure
                .WithConfiguration(document =>
                {
                    document["UserConfig"]!["Logging"] = new JsonObject { ["Type"] = "Console" };
                })
                .WithConfiguration(document =>
                {
                    document["Custom"] = JsonValue.Create(42);
                })
            );

        using var app = builder.Build();
        await app.StartAsync();

        var serviceBusEmulatorResource = builder.Resources.OfType<AzureServiceBusResource>().Single(x => x is { } serviceBusResource && serviceBusResource.IsEmulator);
        var configAnnotation = serviceBusEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/ServiceBus_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = serviceBusEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal("""
            {
              "UserConfig": {
                "Namespaces": [
                  {
                    "Name": "servicebusns",
                    "Queues": [],
                    "Topics": []
                  }
                ],
                "Logging": {
                  "Type": "Console"
                }
              },
              "Custom": 42
            }
            """, configFile.Contents);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureServiceBusEmulator_WithConfigurationFile()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var configJsonPath = Path.GetTempFileName();

        File.WriteAllText(configJsonPath, """
            {
              "UserConfig": {
                "Namespaces": [
                  {
                    "Name": "servicebusns",
                    "Queues": [ { "Name": "queue456" } ],
                    "Topics": []
                  }
                ],
                "Logging": {
                  "Type": "File"
                }
              }
            }
            """);

        var serviceBus = builder.AddAzureServiceBus("servicebusns")
            .RunAsEmulator(configure => configure.WithConfigurationFile(configJsonPath));

        using var app = builder.Build();

        var serviceBusEmulatorResource = builder.Resources.OfType<AzureServiceBusResource>().Single(x => x is { } serviceBusResource && serviceBusResource.IsEmulator);
        var configAnnotation = serviceBusEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/ServiceBus_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = serviceBusEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal(configJsonPath, configFile.SourcePath);

        await app.StopAsync();

        try
        {
            File.Delete(configJsonPath);
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddAzureServiceBusWithEmulator_SetsSqlLifetime(bool isPersistent)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var lifetime = isPersistent ? ContainerLifetime.Persistent : ContainerLifetime.Session;

        var serviceBus = builder.AddAzureServiceBus("sb").RunAsEmulator(configureContainer: builder =>
        {
            builder.WithLifetime(lifetime);
        });

        var sql = builder.Resources.FirstOrDefault(x => x.Name == "sb-mssql");

        Assert.NotNull(sql);

        serviceBus.Resource.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var sbLifetimeAnnotation);
        sql.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var sqlLifetimeAnnotation);

        Assert.Equal(lifetime, sbLifetimeAnnotation?.Lifetime);
        Assert.Equal(lifetime, sqlLifetimeAnnotation?.Lifetime);
    }

    [Fact]
    public void RunAsEmulator_CalledTwice_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("sb").RunAsEmulator();

        Assert.Throws<InvalidOperationException>(() => serviceBus.RunAsEmulator());
    }

    [Fact]
    public void AzureServiceBusHasCorrectConnectionStrings()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("sb");
        var queue = serviceBus.AddServiceBusQueue("queue");
        var topic = serviceBus.AddServiceBusTopic("topic");
        var subscription = topic.AddServiceBusSubscription("sub");

        // Assert that child resources capture entitypath information
        Assert.Equal("{sb.outputs.serviceBusEndpoint}", serviceBus.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={sb.outputs.serviceBusEndpoint};EntityPath=queue", queue.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={sb.outputs.serviceBusEndpoint};EntityPath=topic", topic.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={sb.outputs.serviceBusEndpoint};EntityPath=topic/Subscriptions/sub", subscription.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureServiceBusAppliesAzureFunctionsConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("sb");
        var queue = serviceBus.AddServiceBusQueue("queue");
        var topic = serviceBus.AddServiceBusTopic("topic");
        var subscription = topic.AddServiceBusSubscription("sub");

        var target = new Dictionary<string, object>();
        ((IResourceWithAzureFunctionsConfig)serviceBus.Resource).ApplyAzureFunctionsConfiguration(target, "sb");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__sb__FullyQualifiedNamespace", k),
            k => Assert.Equal("sb__fullyQualifiedNamespace", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)queue.Resource).ApplyAzureFunctionsConfiguration(target, "queue");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__queue__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__queue__QueueOrTopicName", k),
            k => Assert.Equal("queue__fullyQualifiedNamespace", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)topic.Resource).ApplyAzureFunctionsConfiguration(target, "topic");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__topic__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__topic__QueueOrTopicName", k),
            k => Assert.Equal("topic__fullyQualifiedNamespace", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)subscription.Resource).ApplyAzureFunctionsConfiguration(target, "sub");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__sub__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__sub__QueueOrTopicName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__ServiceBus__sub__SubscriptionName", k),
            k => Assert.Equal("sub__fullyQualifiedNamespace", k));
    }
    
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAzureServiceBus(bool useObsoleteMethods)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("sb");

        if (useObsoleteMethods)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            serviceBus
                .AddQueue("queue1")
                .AddQueue("queue2")
                .AddTopic("t1")
                .AddTopic("t2")
                .AddSubscription("t1", "s3");
#pragma warning restore CS0618 // Type or member is obsolete
        }
        else
        {
            serviceBus.AddServiceBusQueue("queue1");
            serviceBus.AddServiceBusQueue("queue2");
            serviceBus.AddServiceBusTopic("t1")
                .AddServiceBusSubscription("s3");
            serviceBus.AddServiceBusTopic("t2");
        }

        serviceBus.Resource.Outputs["serviceBusEndpoint"] = "mynamespaceEndpoint";

        var connectionStringResource = (IResourceWithConnectionString)serviceBus.Resource;

        Assert.Equal("sb", serviceBus.Resource.Name);
        Assert.Equal("mynamespaceEndpoint", await connectionStringResource.GetConnectionStringAsync());
        Assert.Equal("{sb.outputs.serviceBusEndpoint}", connectionStringResource.ConnectionStringExpression.ValueExpression);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, serviceBus.Resource);

        var expected = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{sb.outputs.serviceBusEndpoint}",
              "path": "sb.module.bicep"
            }
            """;
        Assert.Equal(expected, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");

        var sbRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "sb-roles");
        var sbRolesManifest = await GetManifestWithBicep(sbRoles, skipPreparer: true);
        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param sb_outputs_name string

            param principalType string

            param principalId string

            resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
              name: sb_outputs_name
            }

            resource sb_AzureServiceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(sb.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')
                principalType: principalType
              }
              scope: sb
            }
            """;
        output.WriteLine(sbRolesManifest.BicepText);
        Assert.Equal(expectedBicep, sbRolesManifest.BicepText);
    }

    [Fact(Skip = "Azure ServiceBus emulator is not reliable in CI - https://github.com/microsoft/aspire/issues/7066")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureServiceBusEmulator_WithCustomConfig()
    {
        const string queueName = "queue456";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(output);

        var configJsonPath = Path.GetTempFileName();

        File.WriteAllText(configJsonPath,
            $$"""
            {
              "UserConfig": {
                "Namespaces": [
                  {
                    "Name": "sbemulatorns",
                    "Queues": [ { "Name": "{{queueName}}" } ],
                    "Topics": []
                  }
                ],
                "Logging": {
                  "Type": "File"
                }
              }
            }
            """);

        var serviceBus = builder
            .AddAzureServiceBus("servicebusns")
            .RunAsEmulator(configure => configure.WithConfigurationFile(configJsonPath));

        var queueResource = serviceBus.AddServiceBusQueue("queue123", queueName);

        using var app = builder.Build();
        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.Configuration["ConnectionStrings:servicebusns"] = await serviceBus.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        hb.AddAzureServiceBusClient("servicebusns");

        await app.ResourceNotifications.WaitForResourceAsync(serviceBus.Resource.Name, KnownResourceStates.Running, cts.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync(serviceBus.Resource.Name, cts.Token);

        using var host = hb.Build();
        await host.StartAsync();

        var serviceBusClient = host.Services.GetRequiredService<ServiceBusClient>();

        await using var sender = serviceBusClient.CreateSender(queueResource.Resource.QueueName);
        await sender.SendMessageAsync(new ServiceBusMessage("Hello, World!"), cts.Token);

        await using var receiver = serviceBusClient.CreateReceiver(queueResource.Resource.QueueName);
        var message = await receiver.ReceiveMessageAsync(cancellationToken: cts.Token);

        Assert.Equal("Hello, World!", message.Body.ToString());
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("servicebus");
        var container = builder.AddContainer("myContainer", "nginx");
        var method = typeof(AzureServiceBusExtensions)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m =>
                m.Name == nameof(AzureServiceBusExtensions.WithRoleAssignments) &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 3 &&
                m.GetParameters()[2].ParameterType.IsArray &&
                m.GetParameters()[2].ParameterType.GetElementType()?.Name == "AzureServiceBusRole")
            .MakeGenericMethod(typeof(ContainerResource));

        var roleType = method.GetParameters()[2].ParameterType.GetElementType()!;
        var roles = Array.CreateInstance(roleType, 1);
        roles.SetValue(Enum.Parse(roleType, "AzureServiceBusDataSender"), 0);

        var exception = Record.Exception(() =>
            method.Invoke(null, [container, serviceBus, roles]));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_NullRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var serviceBus = builder.AddAzureServiceBus("servicebus");
        var container = builder.AddContainer("myContainer", "nginx");
        var method = typeof(AzureServiceBusExtensions)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m =>
                m.Name == nameof(AzureServiceBusExtensions.WithRoleAssignments) &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 3 &&
                m.GetParameters()[2].ParameterType.IsArray &&
                m.GetParameters()[2].ParameterType.GetElementType()?.Name == "AzureServiceBusRole")
            .MakeGenericMethod(typeof(ContainerResource));

        var exception = Record.Exception(() =>
            method.Invoke(null, [container, serviceBus, null!]));

        Assert.Null(exception);
    }

    [Fact]
    public void RunAsEmulatorAppliesEmulatorResourceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var serviceBus = builder.AddAzureServiceBus("servicebus")
                               .RunAsEmulator();

        // Verify that the EmulatorResourceAnnotation is applied
        Assert.True(serviceBus.Resource.IsEmulator());
        Assert.Contains(serviceBus.Resource.Annotations, a => a is EmulatorResourceAnnotation);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureServiceBusResource()
    {
        // Arrange
        var serviceBusResource = new AzureServiceBusResource("test-servicebus", _ => { });
        var infrastructure = new AzureResourceInfrastructure(serviceBusResource, "test-servicebus");

        // Act - Call AddAsExistingResource twice
        var firstResult = serviceBusResource.AddAsExistingResource(infrastructure);
        var secondResult = serviceBusResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureServiceBusResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-sb-name");
        var existingResourceGroup = builder.AddParameter("existing-sb-rg");

        var serviceBus = builder.AddAzureServiceBus("test-servicebus")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = serviceBus.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
