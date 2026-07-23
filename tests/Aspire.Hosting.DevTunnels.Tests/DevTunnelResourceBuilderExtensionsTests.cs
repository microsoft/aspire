// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001 // Required command validation APIs are experimental.
#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelResourceBuilderExtensionsTests
{
    [Fact]
    public async Task WithReference_InjectsServiceDiscoveryEnvironmentVariablesWhenReferencingOtherResourcesViaTheTunnel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint();
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);
        var consumer = builder.AddResource(new TestResource("consumer"))
            .WithReference(target, tunnel);

        var tunnelPort = tunnel.Resource.Ports.FirstOrDefault();
        Assert.NotNull(tunnelPort);

        tunnelPort.TunnelEndpointAnnotation.AllocatedEndpoint = new(tunnelPort.TunnelEndpointAnnotation, "test123.devtunnels.ms", 443);

        var values = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, serviceProvider: builder.Services.BuildServiceProvider()).DefaultTimeout();

        Assert.Equal("https://test123.devtunnels.ms:443", values["services__target__https__0"]);
        Assert.Equal("https://test123.devtunnels.ms:443", values["TARGET_HTTPS"]);
    }

    [Fact]
    public void AddDevTunnel_WithAnonymousAccess_SetsAllowAnonymousOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tunnel = builder.AddDevTunnel("tunnel")
            .WithAnonymousAccess();

        Assert.True(tunnel.Resource.Options.AllowAnonymous);
    }

    [Fact]
    public void AddDevTunnel_WithSpecificTunnelId_SetsTunnelIdProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tunnel = builder.AddDevTunnel("tunnel", "custom-id");

        Assert.Equal("custom-id", tunnel.Resource.TunnelId);
    }

    [Fact]
    public void AddDevTunnel_WithPersistentLifetime_AddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tunnel = builder.AddDevTunnel("tunnel", "custom-id")
            .WithPersistentLifetime();

        var annotation = Assert.Single(tunnel.Resource.Annotations.OfType<PersistenceAnnotation>());
        Assert.Equal(PersistenceMode.Persistent, annotation.Mode);
    }

    [Fact]
    public void AddDevTunnel_DefaultLifetimeDoesNotAddPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tunnel = builder.AddDevTunnel("tunnel", "custom-id");

        Assert.Empty(tunnel.Resource.Annotations.OfType<PersistenceAnnotation>());
    }

    [Fact]
    public void WithReference_WithAnonymousAccess_SetsPortAllowAnonymousOption()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint();
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target, allowAnonymous: true);

        Assert.Single(tunnel.Resource.Ports);
        var port = tunnel.Resource.Ports.First();
        Assert.True(port.Options.AllowAnonymous);
    }

    [Fact]
    public async Task WithReference_UsesTargetPortForDevTunnelPortWhenAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelPort = Assert.Single(tunnel.Resource.Ports);

        Assert.Equal(5001, await tunnelPort.GetTunnelPortAsync());
    }

    [Fact]
    public async Task WithReference_UsesAllocatedPortForContainerDevTunnelPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddContainer("target", "image")
            .WithHttpEndpoint(port: 5000, targetPort: 8080, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        Assert.Equal(5000, await tunnelPort.GetTunnelPortAsync());
    }

    [Fact]
    public async Task WithReference_ResolvesDynamicTargetPortForDevTunnelPortWhenAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000,
            targetPortExpression: "5001");

        Assert.Equal(5001, await tunnelPort.GetTunnelPortAsync());
    }

    [Fact]
    public async Task WithReference_UsesAllocatedPortForDevTunnelPortWhenTargetPortIsUnavailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        Assert.Equal(5000, await tunnelPort.GetTunnelPortAsync());
    }

    [Fact]
    public async Task AddDevTunnel_WithRegion_UsesResolvedTunnelIdForExecutableArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel", new DevTunnelOptions
        {
            Region = DevTunnelRegion.NorthEurope
        });

#pragma warning disable CS0618 // Type or member is obsolete
        var args = await tunnel.Resource.GetArgumentValuesAsync().DefaultTimeout();
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Equal(["host", "mytunnel.eun1", "--nologo"], args);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_WithRegion_UsesResolvedTunnelIdForPortOperations()
    {
        var client = new TestDevTunnelClient
        {
            PortList = new()
            {
                Ports = [
                    new(5001, "https"),
                    new(6000, "https")
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel", new DevTunnelOptions
        {
            Region = DevTunnelRegion.NorthEurope
        }).WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var calls = client.Calls.ToArray();
        Assert.Contains(calls, call => call.Method == nameof(IDevTunnelClient.CreateTunnelAsync) && call.TunnelId == "mytunnel");
        Assert.Contains(calls, call => call.Method == nameof(IDevTunnelClient.GetPortListAsync) && call.TunnelId == "mytunnel.eun1");
        Assert.Contains(calls, call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.TunnelId == "mytunnel.eun1" && call.PortNumber == 5001);
        Assert.Contains(calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.TunnelId == "mytunnel.eun1" && call.PortNumber == 6000);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_DeletesUnmodeledPortsAfterStartingModeledPorts()
    {
        var client = new TestDevTunnelClient
        {
            PortList = new()
            {
                Ports = [
                    new(5001, "http"),
                    new(5002, "http"),
                    new(6000, "http")
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        client.OnGetPortList = () => tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal(5001, tunnelPort.ActiveTunnelPort);
        var calls = client.Calls.ToArray();
        var createPortIndex = Array.FindIndex(calls, call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5001);
        var listPortsIndex = Array.FindIndex(calls, call => call.Method == nameof(IDevTunnelClient.GetPortListAsync));
        Assert.True(createPortIndex >= 0);
        Assert.True(listPortsIndex >= 0);
        Assert.True(createPortIndex < listPortsIndex);
        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5002);
        Assert.Contains(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 6000);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ReconcilesActivePortWhenTargetEndpointChanges()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal(5001, tunnelPort.ActiveTunnelPort);

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var initialPortState));
        var initialInspectUrl = Assert.Single(initialPortState.Snapshot.Urls, url => string.Equals(url.DisplayProperties.DisplayName, "Inspect", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://mytunnel-5001-inspect.devtunnels.ms/", initialInspectUrl.Url);

        var portEndpointAllocationEventCount = 0;
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                portEndpointAllocationEventCount++;
            }

            return Task.CompletedTask;
        });

        var statesAfterTargetChange = new List<string?>();
        var baselineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var watchTask = Task.Run(async () =>
        {
            try
            {
                var baselineSeen = false;
                await foreach (var evt in notificationService.WatchAsync(watchCts.Token))
                {
                    if (!ReferenceEquals(evt.Resource, tunnelPort))
                    {
                        continue;
                    }

                    if (!baselineSeen)
                    {
                        baselineSeen = true;
                        baselineObserved.TrySetResult();
                        continue;
                    }

                    statesAfterTargetChange.Add(evt.Snapshot.State?.Text);
                }
            }
            catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
            {
            }
        }, watchCts.Token);

        await baselineObserved.Task.DefaultTimeout();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Running
            });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
            {
                var calls = client.Calls.ToArray();
                return calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5002) &&
                    calls.Any(call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5001);
            },
            "Expected dev tunnel port to be recreated for the updated target endpoint.",
            retries: 20);

        await watchCts.CancelAsync();
        await watchTask.DefaultTimeout();

        Assert.Equal(5002, tunnelPort.ActiveTunnelPort);
        Assert.Equal("https://mytunnel-5002.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        Assert.Equal(0, portEndpointAllocationEventCount);
        Assert.DoesNotContain(KnownResourceStates.Starting, statesAfterTargetChange);

        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState));
        var updatedInspectUrl = Assert.Single(updatedPortState.Snapshot.Urls, url => string.Equals(url.DisplayProperties.DisplayName, "Inspect", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://mytunnel-5002-inspect.devtunnels.ms/", updatedInspectUrl.Url);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ReconcilesActivePortWhenContainerAllocatedPortChanges()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddContainer("target", "image")
            .WithHttpEndpoint(port: 5000, targetPort: 8080, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal(5000, tunnelPort.ActiveTunnelPort);

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5000, "http")
                {
                    PortUri = new("https://mytunnel-5000.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var portEndpointAllocationEventCount = 0;
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                portEndpointAllocationEventCount++;
            }

            return Task.CompletedTask;
        });

        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5002);
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
            {
                var calls = client.Calls.ToArray();
                return calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5002) &&
                    calls.Any(call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5000);
            },
            "Expected dev tunnel port to be recreated for the updated container allocated endpoint.",
            retries: 20);

        Assert.Equal(5002, tunnelPort.ActiveTunnelPort);
        Assert.Equal("https://mytunnel-5002.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        Assert.Equal(0, portEndpointAllocationEventCount);

        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState));
        var updatedInspectUrl = Assert.Single(updatedPortState.Snapshot.Urls, url => string.Equals(url.DisplayProperties.DisplayName, "Inspect", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://mytunnel-5002-inspect.devtunnels.ms/", updatedInspectUrl.Url);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ReconcilesActivePortWhenAccessStatusRefreshFails()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };
        client.GetAccessException = new InvalidOperationException("Access unavailable.");

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
            {
                var calls = client.Calls.ToArray();
                return calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5002) &&
                    calls.Any(call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.TunnelId == "mytunnel" && call.PortNumber == 5001);
            },
            "Expected access status failure not to block publishing the reconciled tunnel endpoint.",
            retries: 20);

        Assert.Equal("https://mytunnel-5002.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_KeepsPreviousPortWhenReconciledPortReadyPublishFails()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TunnelPortReadyRetryDelay = TimeSpan.FromMilliseconds(1);
        tunnelPort.TunnelPortReadyRetryCount = 1;
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002),
            "Expected dev tunnel port reconciliation to attempt the updated target endpoint.",
            retries: 20);

        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        Assert.Contains(5001, tunnelPort.StaleTunnelPorts.Keys);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ProtectsPendingStalePortAcrossRestart()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TunnelPortReadyRetryDelay = TimeSpan.FromMilliseconds(1);
        tunnelPort.TunnelPortReadyRetryCount = 1;
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tunnelPort.StaleTunnelPorts.ContainsKey(5001),
            "Expected the previous port to stay pending when the new port URL is not available.",
            retries: 20);

        var stoppedSnapshot = new CustomResourceSnapshot
        {
            ResourceType = "DevTunnel",
            Properties = []
        };
        await builder.Eventing.PublishAsync(
            new ResourceStoppedEvent(tunnel.Resource, app.Services, new ResourceEvent(tunnel.Resource, tunnel.Resource.Name, stoppedSnapshot)),
            CancellationToken.None).DefaultTimeout();

        Assert.Null(tunnelPort.ActiveTunnelPort);
        Assert.Contains(5001, tunnelPort.StaleTunnelPorts.Keys);

        client.PortList = new()
        {
            Ports = [
                new(5001, "http"),
                new(5002, "http"),
                new(7000, "http")
            ]
        };

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
        Assert.Contains(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 7000);
        Assert.Contains(5001, tunnelPort.StaleTunnelPorts.Keys);
    }

    [Fact]
    public async Task OnResourceStopped_ProtectsPublishedActivePortAcrossRestart()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);

        var stoppedSnapshot = new CustomResourceSnapshot
        {
            ResourceType = "DevTunnel",
            Properties = []
        };
        await builder.Eventing.PublishAsync(
            new ResourceStoppedEvent(tunnel.Resource, app.Services, new ResourceEvent(tunnel.Resource, tunnel.Resource.Name, stoppedSnapshot)),
            CancellationToken.None).DefaultTimeout();

        Assert.Null(tunnelPort.ActiveTunnelPort);
        Assert.Contains(5001, tunnelPort.StaleTunnelPorts.Keys);

        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.PortList = new()
        {
            Ports = [
                new(5001, "http"),
                new(5002, "http"),
                new(7000, "http")
            ]
        };

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
        Assert.Contains(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 7000);
        Assert.Contains(5001, tunnelPort.StaleTunnelPorts.Keys);
        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
    }

    [Fact]
    public async Task OnResourceStopped_WinsRaceWithInFlightReadyPublish()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        Task? stopTask = null;
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                var stoppedSnapshot = new CustomResourceSnapshot
                {
                    ResourceType = "DevTunnel",
                    Properties = []
                };
                stopTask = builder.Eventing.PublishAsync(
                    new ResourceStoppedEvent(tunnel.Resource, evt.Services, new ResourceEvent(tunnel.Resource, tunnel.Resource.Name, stoppedSnapshot)),
                    CancellationToken.None);
                stopStarted.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();
        await stopStarted.Task.DefaultTimeout();
        Assert.NotNull(stopTask);
        await stopTask.DefaultTimeout();

        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var portState));
        Assert.Equal(KnownResourceStates.Finished, portState.Snapshot.State?.Text);
        Assert.All(portState.Snapshot.Urls, url => Assert.True(url.IsInactive));
        Assert.Null(tunnelPort.ActiveTunnelPort);
    }

    [Fact]
    public async Task OnResourceReady_DeletesPendingStalePortAfterLaterReadyPublish()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TunnelPortReadyRetryDelay = TimeSpan.FromMilliseconds(1);
        tunnelPort.TunnelPortReadyRetryCount = 1;
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tunnelPort.StaleTunnelPorts.ContainsKey(5001),
            "Expected the previous port to stay pending when the new port URL is not available.",
            retries: 20);

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.DoesNotContain(5001, tunnelPort.StaleTunnelPorts.Keys);
        Assert.Contains(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
        Assert.Equal("https://mytunnel-5002.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
    }

    [Fact]
    public async Task OnResourceReady_DoesNotDeleteStalePortThatIsActiveForSiblingPort()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http")
            .WithHttpsEndpoint(port: 5001, targetPort: 5002, name: "https");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);

        var httpPort = Assert.Single(tunnel.Resource.Ports, port => string.Equals(port.TargetEndpoint.EndpointName, "http", StringComparison.OrdinalIgnoreCase));
        var httpsPort = Assert.Single(tunnel.Resource.Ports, port => string.Equals(port.TargetEndpoint.EndpointName, "https", StringComparison.OrdinalIgnoreCase));
        httpPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            httpPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);
        httpsPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            httpsPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5001);
        httpPort.ActiveTunnelPort = 6000;
        httpPort.StaleTunnelPorts.TryAdd(5001, 0);
        httpPort.LastKnownStatus = new(6000, "http")
        {
            PortUri = new("https://mytunnel-6000.devtunnels.ms")
        };
        httpsPort.ActiveTunnelPort = 5001;
        httpsPort.LastKnownStatus = new(5001, "https")
        {
            PortUri = new("https://mytunnel-5001.devtunnels.ms")
        };
        tunnel.Resource.LastKnownStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [httpPort.LastKnownStatus, httpsPort.LastKnownStatus]
        };

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Contains(5001, httpPort.StaleTunnelPorts.Keys);
        Assert.DoesNotContain(client.Calls, call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001);
    }

    [Fact]
    public async Task OnResourceReady_PublishesUrlBeforeAccessStatusRefreshCompletes()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "http")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            },
            GetAccessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowGetAccess = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        await client.GetAccessStarted.Task.DefaultTimeout();
        client.AllowGetAccess.SetResult();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tunnelPort.LastKnownAccessStatus is not null,
            "Expected access status refresh to complete after URL publication.",
            retries: 20);
    }

    [Fact]
    public async Task AccessStatusRefresh_DoesNotPublishAnonymousAccessForStaleActivePort()
    {
        var client = new TestDevTunnelClient
        {
            AccessStatus = new()
            {
                AccessControlEntries =
                [
                    new("Anonymous", IsDeny: false, IsInherited: false, Subjects: [], Scopes: ["connect"])
                ]
            },
            GetAccessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowGetAccess = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.ActiveTunnelPort = 5001;

        using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(tunnelPort, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        var logger = app.Services.GetRequiredService<ResourceLoggerService>().GetLogger(tunnelPort);
        var refreshTask = DevTunnelAccessStatusRefresh.QueuePortRefresh(client, tunnelPort, notificationService, logger, CancellationToken.None);
        await client.GetAccessStarted.Task.DefaultTimeout();

        tunnelPort.ActiveTunnelPort = 5002;
        client.AllowGetAccess.SetResult();
        await refreshTask.DefaultTimeout();

        Assert.Null(tunnelPort.LastKnownAccessStatus);
        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var portState));
        Assert.DoesNotContain(portState.Snapshot.Properties, property => string.Equals(property.Name, "Anonymous access", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnResourceReady_RemovesAnonymousAccessWhenPortUrlChangesBeforeAccessRefreshCompletes()
    {
        var client = new TestDevTunnelClient
        {
            AccessStatus = new()
            {
                AccessControlEntries =
                [
                    new("Anonymous", IsDeny: false, IsInherited: false, Subjects: [], Scopes: ["connect"])
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
                notificationService.TryGetCurrentState(tunnelPort.Name, out var portState) &&
                portState.Snapshot.Properties.Any(property =>
                    string.Equals(property.Name, "Anonymous access", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(property.Value?.ToString(), "Allowed", StringComparison.OrdinalIgnoreCase)),
            "Expected initial anonymous access metadata to be published.",
            retries: 20);

        client.GetAccessException = new InvalidOperationException("Access refresh failed.");
        client.GetAccessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => string.Equals(tunnelPort.TunnelEndpoint.Url, "https://mytunnel-5002.devtunnels.ms:443", StringComparison.Ordinal),
            "Expected the updated dev tunnel URL to be published.",
            retries: 20);
        await client.GetAccessStarted.Task.DefaultTimeout();

        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState));
        Assert.DoesNotContain(updatedPortState.Snapshot.Properties, property => string.Equals(property.Name, "Anonymous access", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnResourceReady_DoesNotRegressRunningPortToStartingWhenSamePortWasAlreadyPublished()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "http")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        var portBeforeStartedEvents = 0;
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                Interlocked.Increment(ref portBeforeStartedEvents);
            }

            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var currentState));
        Assert.Equal(KnownResourceStates.Running, currentState.Snapshot.State?.Text);

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => Volatile.Read(ref portBeforeStartedEvents) == 2,
            "Expected the initial port start and first ready publish to both emit a port start event.",
            retries: 20);

        var statesAfterDuplicateReady = new List<string?>();
        var baselineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningAfterDuplicateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var watchTask = Task.Run(async () =>
        {
            var baselineSeen = false;
            await foreach (var evt in notificationService.WatchAsync(watchCts.Token))
            {
                if (!ReferenceEquals(evt.Resource, tunnelPort))
                {
                    continue;
                }

                if (!baselineSeen)
                {
                    baselineSeen = true;
                    baselineObserved.TrySetResult();
                    continue;
                }

                statesAfterDuplicateReady.Add(evt.Snapshot.State?.Text);
                if (string.Equals(evt.Snapshot.State?.Text, KnownResourceStates.Running, StringComparisons.ResourceState))
                {
                    runningAfterDuplicateReady.TrySetResult();
                    return;
                }
            }
        }, watchCts.Token);

        await baselineObserved.Task.DefaultTimeout();
        var portBeforeStartedEventsAfterFirstReady = Volatile.Read(ref portBeforeStartedEvents);
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();
        await runningAfterDuplicateReady.Task.DefaultTimeout();
        await watchCts.CancelAsync();
        await watchTask.DefaultTimeout();

        Assert.Equal(portBeforeStartedEventsAfterFirstReady, Volatile.Read(ref portBeforeStartedEvents));
        Assert.DoesNotContain(KnownResourceStates.Starting, statesAfterDuplicateReady);
        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out currentState));
        Assert.Equal(KnownResourceStates.Running, currentState.Snapshot.State?.Text);
    }

    [Fact]
    public async Task OnResourceReady_DoesNotDeletePortThatBecomesActiveDuringStaleDeletion()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TunnelPortReadyRetryDelay = TimeSpan.FromMilliseconds(1);
        tunnelPort.TunnelPortReadyRetryCount = 1;
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tunnelPort.StaleTunnelPorts.ContainsKey(5001),
            "Expected the previous port to stay pending when the new port URL is not available.",
            retries: 20);

        // The target endpoint watcher can observe the ready port as soon as the status is updated below.
        // Install the delete hook first so the test catches either the watcher or the explicit ready event.
        var deletePortStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDeletePort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DeletePortStarted = deletePortStarted;
        client.AllowDeletePort = allowDeletePort;

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;

        var readyTask = builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services));
        Assert.Equal(5001, await deletePortStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var createPort5001CountBeforeTargetChange = client.Calls.Count(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5001);
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5001;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        var targetUpdateTask = notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        Assert.Equal(5002, tunnelPort.ActiveTunnelPort);
        Assert.Equal(createPort5001CountBeforeTargetChange, client.Calls.Count(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5001));

        allowDeletePort.SetResult();
        await Task.WhenAll(readyTask, targetUpdateTask).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Count(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5001) == createPort5001CountBeforeTargetChange + 1,
            "Expected the port to be recreated only after stale deletion completed.",
            retries: 20);

        Assert.False(client.CreatePortCalledWhileDeleteBlocked);
        Assert.Equal(5001, tunnelPort.ActiveTunnelPort);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_RetriesReadyPublishWhenReconciledPortStatusWasUnavailable()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => tunnelPort.ActiveTunnelPort == 5002,
            "Expected dev tunnel port to be recreated for the updated target endpoint.",
            retries: 20);
        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => string.Equals(tunnelPort.TunnelEndpoint.Url, "https://mytunnel-5002.devtunnels.ms:443", StringComparison.Ordinal),
            "Expected dev tunnel endpoint to be published after status became available.",
            retries: 20);

        Assert.Equal(1, client.Calls.Count(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002));
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.DeletePortAsync) && call.PortNumber == 5001),
            "Expected the stale dev tunnel port to be deleted after the replacement endpoint was published.",
            retries: 20);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_MarksPortRuntimeUnhealthyWhenReconcileCreateFails()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var initialPortState));
        Assert.Equal(KnownResourceStates.Running, initialPortState.Snapshot.State?.Text);
        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);

        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.CreatePortException = new InvalidOperationException("Failed to create port.");

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002),
            "Expected dev tunnel port reconciliation to attempt the updated target endpoint.",
            retries: 20);

        Assert.Equal(5001, tunnelPort.ActiveTunnelPort);
        Assert.Equal("https://mytunnel-5001.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
                notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState) &&
                string.Equals(updatedPortState.Snapshot.State?.Text, KnownResourceStates.RuntimeUnhealthy, StringComparisons.ResourceState),
            "Expected failed dev tunnel port reconciliation to mark the port runtime unhealthy.",
            retries: 20);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_MarksPortRuntimeUnhealthyWhenReconcileReadyPollingFails()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TunnelPortReadyRetryDelay = TimeSpan.FromMilliseconds(1);
        tunnelPort.TunnelPortReadyRetryCount = 1;
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: []);

        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
                notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState) &&
                string.Equals(updatedPortState.Snapshot.State?.Text, KnownResourceStates.RuntimeUnhealthy, StringComparisons.ResourceState),
            "Expected dev tunnel port reconciliation with no ready public URL to mark the port runtime unhealthy.",
            retries: 20);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_PublishesEndpointAllocationOnceWhenReadyAndTargetReplayRace()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        var portEndpointAllocationEventCount = 0;
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                Interlocked.Increment(ref portEndpointAllocationEventCount);
            }

            return Task.CompletedTask;
        });

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        tunnel.Resource.LastKnownStatus = client.TunnelStatus;
        tunnelPort.LastKnownStatus = client.TunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        var readyTask = builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services));
        var targetReplayTask = notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await Task.WhenAll(readyTask, targetReplayTask).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => string.Equals(tunnelPort.TunnelEndpoint.Url, "https://mytunnel-5001.devtunnels.ms:443", StringComparison.Ordinal),
            "Expected dev tunnel endpoint to be published.",
            retries: 20);

        Assert.Equal(1, Volatile.Read(ref portEndpointAllocationEventCount));

        await Task.WhenAll(
            builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)),
            notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Running
            })).DefaultTimeout();

        Assert.Equal(1, Volatile.Read(ref portEndpointAllocationEventCount));
    }

    [Fact]
    public async Task OnResourceReady_IgnoresStatusForPreviousActivePort()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        var oldTunnelStatus = new DevTunnelStatus("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5001, "http")
                {
                    PortUri = new("https://mytunnel-5001.devtunnels.ms")
                }
            ]
        };
        client.TunnelStatus = oldTunnelStatus;
        tunnel.Resource.LastKnownStatus = oldTunnelStatus;
        tunnelPort.LastKnownStatus = oldTunnelStatus.Ports.Single();
        tunnel.Resource.LastKnownAccessStatus = client.AccessStatus;
        tunnelPort.LastKnownAccessStatus = client.AccessStatus;
        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => string.Equals(tunnelPort.TunnelEndpoint.Url, "https://mytunnel-5002.devtunnels.ms:443", StringComparison.Ordinal),
            "Expected dev tunnel endpoint to be updated to the new active port.",
            retries: 20);

        tunnel.Resource.LastKnownStatus = oldTunnelStatus;
        tunnelPort.LastKnownStatus = oldTunnelStatus.Ports.Single();

        await builder.Eventing.PublishAsync(new ResourceReadyEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        Assert.Equal(5002, tunnelPort.ActiveTunnelPort);
        Assert.Equal("https://mytunnel-5002.devtunnels.ms:443", tunnelPort.TunnelEndpoint.Url);
        Assert.True(notificationService.TryGetCurrentState(tunnelPort.Name, out var updatedPortState));
        var updatedInspectUrl = Assert.Single(updatedPortState.Snapshot.Urls, url => string.Equals(url.DisplayProperties.DisplayName, "Inspect", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("https://mytunnel-5002-inspect.devtunnels.ms/", updatedInspectUrl.Url);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_IgnoresTargetWatcherBaselineReplay()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "http")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        using var app = builder.Build();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002),
            "Expected the target watcher to process a post-baseline endpoint update.",
            retries: 20);

        var calls = client.Calls.ToArray();
        var firstGetTunnelIndex = Array.FindIndex(calls, call => call.Method == nameof(IDevTunnelClient.GetTunnelAsync));
        var createUpdatedPortIndex = Array.FindIndex(calls, call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002);
        Assert.True(createUpdatedPortIndex >= 0);
        Assert.True(firstGetTunnelIndex == -1 || firstGetTunnelIndex > createUpdatedPortIndex);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ReconcilesBaselineReplayWhenTargetChangedDuringStartup()
    {
        var client = new TestDevTunnelClient();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPort = Assert.Single(tunnel.Resource.Ports);
        tunnelPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            tunnelPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelPort))
            {
                tunnelPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
            }

            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        client.TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
        {
            Ports = [
                new(5002, "http")
                {
                    PortUri = new("https://mytunnel-5002.devtunnels.ms")
                }
            ]
        };

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002),
            "Expected baseline replay to reconcile the target endpoint that changed during startup.",
            retries: 20);

        Assert.Equal(5002, tunnelPort.ActiveTunnelPort);
    }

    [Fact]
    public async Task OnBeforeResourceStarted_ReconcilesOnlyChangedPortDuringBaselineReplay()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5002, "http")
                    {
                        PortUri = new("https://mytunnel-5002.devtunnels.ms")
                    },
                    new(6001, "http")
                    {
                        PortUri = new("https://mytunnel-6001.devtunnels.ms")
                    }
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);
        builder.Services.AddSingleton<IRequiredCommandValidator, TestRequiredCommandValidator>();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http")
            .WithHttpEndpoint(port: 6000, targetPort: 6001, name: "metrics");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);
        var tunnelPorts = tunnel.Resource.Ports.ToArray();
        var httpPort = Assert.Single(tunnelPorts, port => string.Equals(port.TargetEndpoint.EndpointName, "http", StringComparisons.EndpointAnnotationName));
        var metricsPort = Assert.Single(tunnelPorts, port => string.Equals(port.TargetEndpoint.EndpointName, "metrics", StringComparisons.EndpointAnnotationName));
        httpPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            httpPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            5000);
        metricsPort.TargetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(
            metricsPort.TargetEndpoint.EndpointAnnotation,
            "localhost",
            6000);

        var metricsPortEndpointAllocationEventCount = 0;
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, metricsPort))
            {
                metricsPortEndpointAllocationEventCount++;
            }

            return Task.CompletedTask;
        });

        builder.Eventing.Subscribe<BeforeResourceStartedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, httpPort))
            {
                httpPort.TargetEndpoint.EndpointAnnotation.TargetPort = 5002;
            }

            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(target.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnel.Resource, app.Services)).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => client.Calls.Any(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 5002),
            "Expected baseline replay to reconcile only the changed target endpoint.",
            retries: 20);

        Assert.Equal(5002, httpPort.ActiveTunnelPort);
        Assert.Equal(6001, metricsPort.ActiveTunnelPort);
        Assert.Equal(0, metricsPortEndpointAllocationEventCount);
        Assert.Equal(1, client.Calls.Count(call => call.Method == nameof(IDevTunnelClient.CreatePortAsync) && call.PortNumber == 6001));
    }

    [Fact]
    public async Task DevTunnelHealthCheck_WithRegion_UsesResolvedTunnelIdForTunnelAndAccessOperations()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel.eun1", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "https")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            }
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel", new DevTunnelOptions
        {
            Region = DevTunnelRegion.NorthEurope
        }).WithReference(target);

        using var app = builder.Build();
        var healthCheck = new DevTunnelHealthCheck(
            client,
            app.Services.GetRequiredService<LoggedOutNotificationManager>(),
            tunnel.Resource,
            app.Services.GetRequiredService<ILogger<DevTunnelHealthCheck>>());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext()).DefaultTimeout();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        var calls = client.Calls.ToArray();
        Assert.Contains(calls, call => call.Method == nameof(IDevTunnelClient.GetTunnelAsync) && call.TunnelId == "mytunnel.eun1");
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
            {
                calls = client.Calls.ToArray();
                return calls.Any(call => call.Method == nameof(IDevTunnelClient.GetAccessAsync) && call.TunnelId == "mytunnel.eun1" && call.PortNumber is null) &&
                       calls.Any(call => call.Method == nameof(IDevTunnelClient.GetAccessAsync) && call.TunnelId == "mytunnel.eun1" && call.PortNumber == 5001);
            },
            "Expected access refresh to use the resolved tunnel id.",
            retries: 20);
    }

    [Fact]
    public async Task DevTunnelHealthCheck_DoesNotWaitForAccessStatusRefresh()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "https")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            },
            GetAccessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AllowGetAccess = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);

        using var app = builder.Build();
        var healthCheck = new DevTunnelHealthCheck(
            client,
            app.Services.GetRequiredService<LoggedOutNotificationManager>(),
            tunnel.Resource,
            app.Services.GetRequiredService<ILogger<DevTunnelHealthCheck>>());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext()).DefaultTimeout();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        await client.GetAccessStarted.Task.DefaultTimeout();
        client.AllowGetAccess.SetResult();
    }

    [Fact]
    public async Task DevTunnelHealthCheck_IsHealthyWhenAccessStatusRefreshFails()
    {
        var client = new TestDevTunnelClient
        {
            TunnelStatus = new("mytunnel", HostConnections: 1, ClientConnections: 0, Description: "", Labels: [])
            {
                Ports = [
                    new(5001, "https")
                    {
                        PortUri = new("https://mytunnel-5001.devtunnels.ms")
                    }
                ]
            },
            GetAccessException = new InvalidOperationException("Access unavailable.")
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);

        using var app = builder.Build();
        var healthCheck = new DevTunnelHealthCheck(
            client,
            app.Services.GetRequiredService<LoggedOutNotificationManager>(),
            tunnel.Resource,
            app.Services.GetRequiredService<ILogger<DevTunnelHealthCheck>>());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext()).DefaultTimeout();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Null(tunnel.Resource.LastKnownAccessStatus);
        var portResource = Assert.Single(tunnel.Resource.Ports);
        Assert.Null(portResource.LastKnownAccessStatus);
    }

    [Fact]
    public async Task AccessStatusRefresh_DoesNotClearSiblingPortAccessStatusWhenOnePortFails()
    {
        var client = new TestDevTunnelClient
        {
            GetAccessExceptionFactory = portNumber => portNumber == 5001
                ? new InvalidOperationException("Port access unavailable.")
                : null
        };

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDevTunnelClient>(client);

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(port: 5000, targetPort: 5001, name: "http")
            .WithHttpEndpoint(port: 6000, targetPort: 6001, name: "metrics");
        var tunnel = builder.AddDevTunnel("tunnel", "mytunnel")
            .WithReference(target);

        var httpPort = Assert.Single(tunnel.Resource.Ports, port => string.Equals(port.TargetEndpoint.EndpointName, "http", StringComparisons.EndpointAnnotationName));
        var metricsPort = Assert.Single(tunnel.Resource.Ports, port => string.Equals(port.TargetEndpoint.EndpointName, "metrics", StringComparisons.EndpointAnnotationName));
        httpPort.ActiveTunnelPort = 5001;
        metricsPort.ActiveTunnelPort = 6001;
        var staleMetricsAccessStatus = new DevTunnelAccessStatus();
        metricsPort.LastKnownAccessStatus = staleMetricsAccessStatus;

        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ResourceLoggerService>().GetLogger(tunnel.Resource);

        await DevTunnelAccessStatusRefresh.QueueTunnelAndPortRefresh(client, tunnel.Resource, logger, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(tunnel.Resource.LastKnownAccessStatus);
        Assert.Null(httpPort.LastKnownAccessStatus);
        Assert.NotNull(metricsPort.LastKnownAccessStatus);
    }

    [Fact]
    public void GetEndpoint_WithResourceAndEndpointName_ReturnsTunnelEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelEndpoint = tunnel.GetEndpoint(target.Resource, "https");

        Assert.NotNull(tunnelEndpoint);
        Assert.Equal(target.Resource, tunnelEndpoint.Resource);
        Assert.Equal(DevTunnelPortResource.TunnelEndpointName, tunnelEndpoint.EndpointName);
    }

    [Fact]
    public void GetEndpoint_WithResourceBuilderAndEndpointName_ReturnsTunnelEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var tunnelEndpoint = tunnel.GetEndpoint(target, "https");

        Assert.NotNull(tunnelEndpoint);
        Assert.Equal(target.Resource, tunnelEndpoint.Resource);
        Assert.Equal(DevTunnelPortResource.TunnelEndpointName, tunnelEndpoint.EndpointName);
    }

    [Fact]
    public void GetEndpoint_WithEndpointReference_ReturnsTunnelEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var targetEndpoint = target.GetEndpoint("https");
        var tunnelEndpoint = tunnel.GetEndpoint(targetEndpoint);

        Assert.NotNull(tunnelEndpoint);
        Assert.Equal(target.Resource, tunnelEndpoint.Resource);
        Assert.Equal(DevTunnelPortResource.TunnelEndpointName, tunnelEndpoint.EndpointName);
    }

    [Fact]
    public void GetEndpoint_WithResourceAndEndpointName_ReturnsEndpointWithErrorWhenEndpointNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var endpointRef = tunnel.GetEndpoint(target.Resource, "nonexistent");

        Assert.NotNull(endpointRef);
        Assert.False(endpointRef.Exists);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = endpointRef.EndpointAnnotation);
        Assert.Equal("The dev tunnel 'tunnel' has not been associated with 'nonexistent' on resource 'target'. Use 'WithReference(target)' on the dev tunnel to expose this endpoint.", ex.Message);
    }

    [Fact]
    public void GetEndpoint_WithEndpointReference_ReturnsEndpointWithErrorWhenEndpointNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var target2 = builder.AddProject<ProjectA>("target2")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var target2Endpoint = target2.GetEndpoint("https");
        var endpointRef = tunnel.GetEndpoint(target2Endpoint);

        Assert.NotNull(endpointRef);
        Assert.False(endpointRef.Exists);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = endpointRef.EndpointAnnotation);
        Assert.Equal("The dev tunnel 'tunnel' has not been associated with 'https' on resource 'target2'. Use 'WithReference(target2)' on the dev tunnel to expose this endpoint.", ex.Message);
    }

    [Fact]
    public void GetEndpoint_WithResourceAndEndpointName_ReturnsEndpointWithErrorWhenResourceNotReferenced()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel");

        var endpointRef = tunnel.GetEndpoint(target.Resource, "https");

        Assert.NotNull(endpointRef);
        Assert.False(endpointRef.Exists);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = endpointRef.EndpointAnnotation);
        Assert.Equal("The dev tunnel 'tunnel' has not been associated with 'https' on resource 'target'. Use 'WithReference(target)' on the dev tunnel to expose this endpoint.", ex.Message);
    }

    [Fact]
    public void GetEndpoint_WithMultipleEndpoints_ReturnsCorrectTunnelEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var target = builder.AddProject<ProjectA>("target")
            .WithHttpEndpoint(name: "http")
            .WithHttpsEndpoint(name: "https");
        var tunnel = builder.AddDevTunnel("tunnel")
            .WithReference(target);

        var httpTunnelEndpoint = tunnel.GetEndpoint(target.Resource, "http");
        var httpsTunnelEndpoint = tunnel.GetEndpoint(target.Resource, "https");

        Assert.NotNull(httpTunnelEndpoint);
        Assert.NotNull(httpsTunnelEndpoint);
        Assert.Equal(DevTunnelPortResource.TunnelEndpointName, httpTunnelEndpoint.EndpointName);
        Assert.Equal(DevTunnelPortResource.TunnelEndpointName, httpsTunnelEndpoint.EndpointName);

        // Verify they reference different ports (implicitly through the annotation)
        Assert.NotSame(httpTunnelEndpoint.EndpointAnnotation, httpsTunnelEndpoint.EndpointAnnotation);
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class TestResource(string name) : Resource(name), IResourceWithEnvironment
    {

    }

    private sealed class TestRequiredCommandValidator : IRequiredCommandValidator
    {
        public Task<RequiredCommandValidationResult> ValidateAsync(IResource resource, RequiredCommandAnnotation annotation, CancellationToken cancellationToken)
            => Task.FromResult(RequiredCommandValidationResult.Success());
    }
}
