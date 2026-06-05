// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EventHubs;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEventHubsExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnEventHubsEmulatorBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddAzureEventHubs("resource")
                              .RunAsEmulator()
                              .WithHealthCheck("blocking_check");
        resource.AddHub("hubx");

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

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "random")]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureEventHubsEmulatorResource(bool referenceHub, string? hubName)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var eventHubns = builder.AddAzureEventHubs("eventhubns")
            .RunAsEmulator();
        var resourceName = "hub";
        var eventHub = eventHubns.AddHub(resourceName, hubName);

        using var app = builder.Build();
        await app.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(eventHubns.Resource.Name, cts.Token);

        var hb = Host.CreateApplicationBuilder();

        if (referenceHub)
        {
            hb.Configuration["ConnectionStrings:hub"] = await eventHub.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
            hb.AddAzureEventHubProducerClient("hub");
            hb.AddAzureEventHubConsumerClient("hub");
        }
        else
        {
            hb.Configuration["ConnectionStrings:eventhubns"] = await eventHubns.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
            hb.AddAzureEventHubProducerClient("eventhubns", settings => settings.EventHubName = eventHub.Resource.HubName);
            hb.AddAzureEventHubConsumerClient("eventhubns", settings => settings.EventHubName = eventHub.Resource.HubName);
        }

        using var host = hb.Build();
        await host.StartAsync();

        var producerClient = host.Services.GetRequiredService<EventHubProducerClient>();
        var consumerClient = host.Services.GetRequiredService<EventHubConsumerClient>();

        // If no exception is thrown when awaited, the Event Hubs service has acknowledged
        // receipt and assumed responsibility for delivery of the set of events to its partition.
        await producerClient.SendAsync([new EventData(Encoding.UTF8.GetBytes("hello worlds"))]);

        await foreach (var partitionEvent in consumerClient.ReadEventsAsync(new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(5) }))
        {
            Assert.Equal("hello worlds", Encoding.UTF8.GetString(partitionEvent.Data.EventBody.ToArray()));
            break;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureEventHubsHealthChecksUsesSettingsEventHubName(bool useSettings)
    {
        const string hubName = "myhub";

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var eventHubns = builder.AddAzureEventHubs("eventhubns")
            .RunAsEmulator();
        var resourceName = "hub";
        var eventHub = eventHubns.AddHub(resourceName, hubName);

        using var app = builder.Build();
        await app.StartAsync();

        await app.ResourceNotifications.WaitForResourceHealthyAsync(eventHubns.Resource.Name, cts.Token);

        var hb = Host.CreateApplicationBuilder();

        if (useSettings)
        {
            hb.Configuration["ConnectionStrings:eventhubns"] = await eventHubns.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
            hb.AddAzureEventHubProducerClient("eventhubns", settings => settings.EventHubName = eventHub.Resource.HubName);
            hb.AddAzureEventHubConsumerClient("eventhubns", settings => settings.EventHubName = eventHub.Resource.HubName);
        }
        else
        {
            hb.Configuration["ConnectionStrings:eventhubns"] = await eventHubns.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None) + $";EntityPath={hubName};";
            hb.AddAzureEventHubProducerClient("eventhubns");
            hb.AddAzureEventHubConsumerClient("eventhubns");
        }

        using var host = hb.Build();
        await host.StartAsync();

        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();
        var healthCheckReport = await healthCheckService.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, healthCheckReport.Status);
    }

    [Fact]
    public void AzureEventHubsUseEmulatorCallbackWithWithDataBindMountResultsInBindMountAnnotationWithDefaultPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator(configureContainer: builder =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.WithDataBindMount();
#pragma warning restore CS0618 // Type or member is obsolete
        });

        // Ignoring the annotation created for the custom Config.json file
        var volumeAnnotation = eventHubs.Resource.Annotations.OfType<ContainerMountAnnotation>().Single(a => !a.Target.Contains("Config.json"));
        Assert.Equal(Path.Combine(builder.AppHostDirectory, ".eventhubs", "eh"), volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void AzureEventHubsUseEmulatorCallbackWithWithDataBindMountResultsInBindMountAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator(configureContainer: builder =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.WithDataBindMount("mydata");
#pragma warning restore CS0618 // Type or member is obsolete
        });

        // Ignoring the annotation created for the custom Config.json file
        var volumeAnnotation = eventHubs.Resource.Annotations.OfType<ContainerMountAnnotation>().Single(a => !a.Target.Contains("Config.json"));
        Assert.Equal(Path.Combine(builder.AppHostDirectory, "mydata"), volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.BindMount, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void AzureEventHubsUseEmulatorCallbackWithWithDataVolumeResultsInVolumeAnnotationWithDefaultName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator(configureContainer: builder =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.WithDataVolume();
#pragma warning restore CS0618 // Type or member is obsolete
        });

        // Ignoring the annotation created for the custom Config.json file
        var volumeAnnotation = eventHubs.Resource.Annotations.OfType<ContainerMountAnnotation>().Single(a => !a.Target.Contains("Config.json"));
        Assert.Equal($"{builder.GetVolumePrefix()}-eh-data", volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Fact]
    public void AzureEventHubsUseEmulatorCallbackWithWithDataVolumeResultsInVolumeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator(configureContainer: builder =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.WithDataVolume("mydata");
#pragma warning restore CS0618 // Type or member is obsolete
        });

        // Ignoring the annotation created for the custom Config.json file
        var volumeAnnotation = eventHubs.Resource.Annotations.OfType<ContainerMountAnnotation>().Single(a => !a.Target.Contains("Config.json"));
        Assert.Equal("mydata", volumeAnnotation.Source);
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.Equal(ContainerMountType.Volume, volumeAnnotation.Type);
        Assert.False(volumeAnnotation.IsReadOnly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(8081)]
    [InlineData(9007)]
    public void AzureEventHubsWithEmulatorGetsExpectedPort(int? port = null)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs").RunAsEmulator(configureContainer: builder =>
        {
            builder.WithHostPort(port);
        });

        var endpoints = eventHubs.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

        Assert.Equal(2, endpoints.Count);

        Assert.Equal("emulator", endpoints[0].Name);
        Assert.Equal(port, endpoints[0].Port);

        Assert.Equal("emulatorhealth", endpoints[1].Name);
        Assert.Null(endpoints[1].Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.3.97-preview")]
    [InlineData("1.0.7")]
    public void AzureEventHubsWithEmulatorGetsExpectedImageTag(string? imageTag)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs");

        eventHubs.RunAsEmulator(container =>
        {
            if (!string.IsNullOrEmpty(imageTag))
            {
                container.WithImageTag(imageTag);
            }
        });

        var containerImageAnnotation = eventHubs.Resource.Annotations.OfType<ContainerImageAnnotation>().FirstOrDefault();
        Assert.NotNull(containerImageAnnotation);

        Assert.Equal(imageTag ?? EventHubsEmulatorContainerImageTags.Tag, containerImageAnnotation.Tag);
        Assert.Equal(EventHubsEmulatorContainerImageTags.Registry, containerImageAnnotation.Registry);
        Assert.Equal(EventHubsEmulatorContainerImageTags.Image, containerImageAnnotation.Image);
    }

    [Fact]
    public async Task CanSetHubAndConsumerGroupName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh");

        eventHubs.AddHub("hub-resource", "hub-name")
            .WithProperties(hub => hub.PartitionCount = 3)
            .AddConsumerGroup("cg1", "group-name");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(model, eventHubs.Resource);

        var ehRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "eh-roles");
        var (ehRolesManifest, ehRolesBicep) = await AzureManifestUtils.GetManifestWithBicep(ehRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(ehRolesManifest.ToString(), "json")
              .AppendContentAsFile(ehRolesBicep, "bicep");
              
    }

    [Fact]
    public async Task AzureEventHubsEmulatorResourceInitializesProvisioningModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        global::Azure.Provisioning.EventHubs.EventHub? hub = null;
        global::Azure.Provisioning.EventHubs.EventHubsConsumerGroup? cg = null;

        var eventHubs = builder.AddAzureEventHubs("eh")
            .ConfigureInfrastructure(infrastructure =>
            {
                hub = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.EventHubs.EventHub>().Single();
                cg = infrastructure.GetProvisionableResources().OfType<global::Azure.Provisioning.EventHubs.EventHubsConsumerGroup>().Single();
            });

        eventHubs.AddHub("hub1")
            .WithProperties(hub => hub.PartitionCount = 4)
            .AddConsumerGroup("cg1");

        using var app = builder.Build();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(eventHubs.Resource);

        Assert.NotNull(hub);
        Assert.Equal("hub1", hub.Name.Value);
        Assert.Equal(4, hub.PartitionCount.Value);

        Assert.NotNull(cg);
        Assert.Equal("cg1", cg.Name.Value);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureEventHubsEmulatorResourceGeneratesConfigJson()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var eventHubs = builder.AddAzureEventHubs("eh")
            .RunAsEmulator();

        eventHubs.AddHub("hub1")
            .WithProperties(hub => hub.PartitionCount = 4)
            .AddConsumerGroup("cg1");

        using var app = builder.Build();
        await app.StartAsync();

        var eventHubsEmulatorResource = builder.Resources.OfType<AzureEventHubsResource>().Single(x => x is { } eventHubsResource && eventHubsResource.IsEmulator);
        var configAnnotation = eventHubsEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/Eventhubs_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = eventHubsEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal(/*json*/"""
        {
          "UserConfig": {
            "NamespaceConfig": [
              {
                "Type": "EventHub",
                "Name": "emulatorNs1",
                "Entities": [
                  {
                    "Name": "hub1",
                    "PartitionCount": 4,
                    "ConsumerGroups": [
                      {
                        "Name": "cg1"
                      }
                    ]
                  }
                ]
              }
            ],
            "LoggingConfig": {
              "Type": "File"
            }
          }
        }
        """, configFile.Contents);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AzureEventHubsEmulatorResourceGeneratesConfigJsonWithCustomizations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var eventHubs = builder
            .AddAzureEventHubs("eh")
            .RunAsEmulator(configure => configure
            .WithConfiguration(document =>
            {
                document["UserConfig"]!["LoggingConfig"] = new JsonObject { ["Type"] = "Console" };
            })
            .WithConfiguration(document =>
            {
                document["Custom"] = JsonValue.Create(42);
            }));

        eventHubs.AddHub("hub1");

        using var app = builder.Build();
        await app.StartAsync();

        var eventHubsEmulatorResource = builder.Resources.OfType<AzureEventHubsResource>().Single(x => x is { } eventHubsResource && eventHubsResource.IsEmulator);
        var configAnnotation = eventHubsEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/Eventhubs_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = eventHubsEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
        var configFile = Assert.IsType<ContainerFile>(Assert.Single(configFiles));
        Assert.Equal("Config.json", configFile.Name);

        Assert.Equal(/*json*/"""
        {
          "UserConfig": {
            "NamespaceConfig": [
              {
                "Type": "EventHub",
                "Name": "emulatorNs1",
                "Entities": [
                  {
                    "Name": "hub1",
                    "PartitionCount": 1,
                    "ConsumerGroups": []
                  }
                ]
              }
            ],
            "LoggingConfig": {
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
    public async Task AzureEventHubsEmulator_WithConfigurationFile()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var configJsonPath = Path.GetTempFileName();

        var source = /*json*/"""
        {
          "UserConfig": {
            "NamespaceConfig": [
              {
                "Type": "EventHub",
                "Name": "emulatorNs1",
                "Entities": [
                  {
                    "Name": "hub1",
                    "PartitionCount": 2,
                    "ConsumerGroups": []
                  }
                ]
              }
            ],
            "LoggingConfig": {
              "Type": "Console"
            }
          }
        }
        """;

        File.WriteAllText(configJsonPath, source);

        var eventHubs = builder.AddAzureEventHubs("eh")
            .RunAsEmulator(configure => configure.WithConfigurationFile(configJsonPath));

        using var app = builder.Build();
        await app.StartAsync();

        var eventHubsEmulatorResource = builder.Resources.OfType<AzureEventHubsResource>().Single(x => x is { } eventHubsResource && eventHubsResource.IsEmulator);
        var configAnnotation = eventHubsEmulatorResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>().Single();

        Assert.Equal("/Eventhubs_Emulator/ConfigFiles", configAnnotation.DestinationPath);
        var configFiles = await configAnnotation.Callback(new ContainerFileSystemCallbackContext { Model = eventHubsEmulatorResource, ServiceProvider = app.Services }, CancellationToken.None);
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
    public void AddAzureEventHubsWithEmulator_SetsStorageLifetime(bool isPersistent)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var lifetime = isPersistent ? Lifetime.Persistent : Lifetime.Session;

        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator(configureContainer: builder =>
        {
            _ = lifetime switch
            {
                Lifetime.Session => builder.WithSessionLifetime(),
                Lifetime.Persistent => builder.WithPersistentLifetime(),
                _ => throw new InvalidOperationException($"Unknown resource lifetime '{Enum.GetName(typeof(Lifetime), lifetime)}'.")
            };
        });

        var azurite = builder.Resources.FirstOrDefault(x => x.Name == "eh-storage");

        Assert.NotNull(azurite);

        var sourceResource = GetPersistenceReferenceSource(azurite);
        Assert.Same(eventHubs.Resource.Annotations, sourceResource.Annotations);

        var persistenceAnnotation = Assert.Single(eventHubs.Resource.Annotations.OfType<PersistenceAnnotation>());
        Assert.Equal(ToPersistenceMode(lifetime), persistenceAnnotation.Mode);
    }

    [Fact]
    public void AddAzureEventHubsWithEmulator_DoesNotSetStorageLifetimeWithoutContainerConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzureEventHubs("eh").RunAsEmulator();

        var azurite = builder.Resources.FirstOrDefault(x => x.Name == "eh-storage");

        Assert.NotNull(azurite);
        Assert.Empty(azurite.Annotations.OfType<PersistenceAnnotation>());
    }

    [Fact]
    public void RunAsEmulator_CalledTwice_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eh").RunAsEmulator();

        Assert.Throws<InvalidOperationException>(() => eventHubs.RunAsEmulator());
    }

    private static IResource GetPersistenceReferenceSource(IResource resource)
    {
        var annotation = Assert.Single(resource.Annotations.OfType<PersistenceAnnotation>());
        return Assert.IsAssignableFrom<IResource>(annotation.SourceResource);
    }

    private static PersistenceMode ToPersistenceMode(Lifetime lifetime) =>
        lifetime switch
        {
            Lifetime.Session => PersistenceMode.Session,
            Lifetime.Persistent => PersistenceMode.Persistent,
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, null)
        };

    [Fact]
    public void AzureEventHubsHasCorrectConnectionStrings()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var eventHubs = builder.AddAzureEventHubs("eh");
        var eventHub = eventHubs.AddHub("hub1");
        var consumerGroup = eventHub.AddConsumerGroup("cg1");

        Assert.Equal("{eh.outputs.eventHubsEndpoint}", eventHubs.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={eh.outputs.eventHubsEndpoint};EntityPath=hub1", eventHub.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={eh.outputs.eventHubsEndpoint};EntityPath=hub1;ConsumerGroup=cg1", consumerGroup.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureEventHubsAppliesAzureFunctionsConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var eventHubs = builder.AddAzureEventHubs("eh");
        var eventHub = eventHubs.AddHub("hub1");
        var consumerGroup = eventHub.AddConsumerGroup("cg1");

        var target = new Dictionary<string, object>();
        ((IResourceWithAzureFunctionsConfig)eventHubs.Resource).ApplyAzureFunctionsConfiguration(target, "eh");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__eh__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__eh__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__eh__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__eh__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__eh__FullyQualifiedNamespace", k),
            k => Assert.Equal("eh__fullyQualifiedNamespace", k));

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)eventHub.Resource).ApplyAzureFunctionsConfiguration(target, "hub1");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__hub1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__hub1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__hub1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__hub1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__hub1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__hub1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__hub1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__hub1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__hub1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__hub1__FullyQualifiedNamespace", k),
            k => Assert.Equal("hub1__fullyQualifiedNamespace", k));
        Assert.Equal("hub1", target["Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__hub1__EventHubName"]);

        target.Clear();
        ((IResourceWithAzureFunctionsConfig)consumerGroup.Resource).ApplyAzureFunctionsConfiguration(target, "cg1");
        Assert.Collection(target.Keys.OrderBy(k => k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__cg1__ConsumerGroup", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__cg1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__cg1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__cg1__ConsumerGroup", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__cg1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__cg1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__cg1__ConsumerGroup", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__cg1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__cg1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__cg1__ConsumerGroup", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__cg1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__cg1__FullyQualifiedNamespace", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__cg1__ConsumerGroup", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__cg1__EventHubName", k),
            k => Assert.Equal("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__cg1__FullyQualifiedNamespace", k),
            k => Assert.Equal("cg1__fullyQualifiedNamespace", k));
        Assert.Equal("cg1", target["Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__cg1__ConsumerGroup"]);
        Assert.Equal("hub1", target["Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__cg1__EventHubName"]);
    }

    [Fact]
    public void RunAsEmulatorAppliesEmulatorResourceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("eventhubs")
                              .RunAsEmulator();

        // Verify that the EmulatorResourceAnnotation is applied
        Assert.True(eventHubs.Resource.IsEmulator());
        Assert.Contains(eventHubs.Resource.Annotations, a => a is EmulatorResourceAnnotation);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureEventHubsResource()
    {
        // Arrange
        var eventHubsResource = new AzureEventHubsResource("test-eventhubs", _ => { });
        var infrastructure = new AzureResourceInfrastructure(eventHubsResource, "test-eventhubs");

        // Act - Call AddAsExistingResource twice
        var firstResult = eventHubsResource.AddAsExistingResource(infrastructure);
        var secondResult = eventHubsResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureEventHubsResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-eventhubs-name");
        var existingResourceGroup = builder.AddParameter("existing-eventhubs-rg");

        var eventHubs = builder.AddAzureEventHubs("test-eventhubs")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = eventHubs.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
