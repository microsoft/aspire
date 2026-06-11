// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.TestUtilities;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Nats.Tests;

public class NatsFunctionalTests(ITestOutputHelper testOutputHelper)
{
    private const string StreamName = "test-stream";
    private const string SubjectName = "test-subject";

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyNatsResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var nats = builder.AddNats("nats")
            .WithJetStream();

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Listening for client connections", nats.Resource.Name);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        hb.Configuration[$"ConnectionStrings:{nats.Resource.Name}"] = await nats.Resource.ConnectionStringExpression.GetValueAsync(default);

        hb.AddNatsClient("nats", configureOptions: opts =>
        {
            var jsonRegistry = new NatsJsonContextSerializerRegistry(AppJsonContext.Default);
            return opts with { SerializerRegistry = jsonRegistry };
        });

        hb.AddNatsJetStream();

        using var host = hb.Build();

        await host.StartAsync();

        var jetStream = host.Services.GetRequiredService<INatsJSContext>();

        await CreateTestData(jetStream, default);
        await ConsumeTestData(jetStream, default);
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData(null, null)]
    [InlineData("nats", null)]
    [InlineData(null, "password")]
    [InlineData("nats", "password")]
    public async Task AuthenticationShouldWork(string? user, string? password)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
      
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var usernameParameter = user is null ? null : builder.AddParameter("user", user);
        var passwordParameter = password is null ? null : builder.AddParameter("pass", password);

        var nats = builder.AddNats("nats", userName: usernameParameter, password: passwordParameter);

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Listening for client connections", nats.Resource.Name);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        var connectionString = await nats.Resource.ConnectionStringExpression.GetValueAsync(default);
        hb.Configuration[$"ConnectionStrings:{nats.Resource.Name}"] = connectionString;

        hb.AddNatsClient("nats", configureOptions: opts =>
        {
            var jsonRegistry = new NatsJsonContextSerializerRegistry(AppJsonContext.Default);
            return opts with { SerializerRegistry = jsonRegistry };
        });

        using var host = hb.Build();

        await host.StartAsync();

        var natsConnection = host.Services.GetRequiredService<INatsConnection>();
        await natsConnection.ConnectAsync();
        Assert.Equal(NatsConnectionState.Open, natsConnection.ConnectionState);
    }

    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData("user", "wrong-password")]
    [InlineData("wrong-user", "password")]
    [InlineData(null, null)]
    public async Task AuthenticationShouldFailOnWrongOrMissingCredentials(string? user, string? password)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var usernameParameter = builder.AddParameter("user", "user");
        var passwordParameter = builder.AddParameter("pass", "password");

        var nats = builder.AddNats("nats", userName: usernameParameter, password: passwordParameter);

        using var app = builder.Build();

        await app.StartAsync();

        await app.WaitForTextAsync("Listening for client connections", nats.Resource.Name);

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);

        var connectionString = await nats.Resource.ConnectionStringExpression.GetValueAsync(default);
        var modifiedConnectionString = user is null
            ? connectionString!.Replace(new Uri(connectionString).UserInfo, null)
            : connectionString!.Replace("user", user).Replace("password", password);

        hb.Configuration[$"ConnectionStrings:{nats.Resource.Name}"] = modifiedConnectionString;

        hb.AddNatsClient("nats", configureOptions: opts =>
        {
            var jsonRegistry = new NatsJsonContextSerializerRegistry(AppJsonContext.Default);
            return opts with { SerializerRegistry = jsonRegistry };
        });

        using var host = hb.Build();

        await host.StartAsync();

        var natsConnection = host.Services.GetRequiredService<INatsConnection>();

        var exception = await Assert.ThrowsAsync<NatsException>(async () => await natsConnection.ConnectAsync());
        Assert.IsType<NatsServerException>(exception.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var nats1 = builder1.AddNats("nats")
                .WithJetStream();

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(nats1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                nats1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                nats1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Listening for client connections", nats1.Resource.Name);
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{nats1.Resource.Name}"] = await nats1.Resource.ConnectionStringExpression.GetValueAsync(default);

                    hb.AddNatsClient("nats", configureOptions: opts =>
                    {
                        var jsonRegistry = new NatsJsonContextSerializerRegistry(AppJsonContext.Default);
                        return opts with { SerializerRegistry = jsonRegistry };
                    });

                    hb.AddNatsJetStream();

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        var jetStream = host.Services.GetRequiredService<INatsJSContext>();
                        await CreateTestData(jetStream, default);
                        await ConsumeTestData(jetStream, default);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var nats2 = builder2.AddNats("nats")
                .WithJetStream();

            if (useVolume)
            {
                nats2.WithDataVolume(volumeName);
            }
            else
            {
                nats2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();

                await app.WaitForTextAsync("Listening for client connections", nats2.Resource.Name);
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);

                    hb.Configuration[$"ConnectionStrings:{nats2.Resource.Name}"] = await nats2.Resource.ConnectionStringExpression.GetValueAsync(default);
                    hb.AddNatsClient("nats", configureOptions: opts =>
                    {
                        var jsonRegistry = new NatsJsonContextSerializerRegistry(AppJsonContext.Default);
                        return opts with { SerializerRegistry = jsonRegistry };
                    });

                    hb.AddNatsJetStream();

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();

                        var jetStream = host.Services.GetRequiredService<INatsJSContext>();
                        await ConsumeTestData(jetStream, default);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }
        }
        finally
        {
            if (volumeName is not null)
            {
                DockerUtils.AttemptDeleteDockerVolume(volumeName);
            }

            if (bindMountPath is not null)
            {
                try
                {
                    Directory.Delete(bindMountPath, recursive: true);
                }
                catch
                {
                    // Don't fail test if we can't clean the temporary folder
                }
            }
        }
    }

    private static async Task ConsumeTestData(INatsJSContext jetStream, CancellationToken token)
    {
        var stream = await jetStream.GetStreamAsync(StreamName, cancellationToken: token);
        var consumer = await stream.CreateOrderedConsumerAsync(cancellationToken: token);

        var events = new List<AppEvent>();
        await foreach (var msg in consumer.ConsumeAsync<AppEvent>(cancellationToken: token))
        {
            events.Add(msg.Data!);
            await msg.AckAsync(cancellationToken: token);
            if (msg.Metadata?.NumPending == 0)
            {
                break;
            }
        }

        for (var i = 0; i < 10; i++)
        {
            var @event = events[i];
            Assert.Equal($"test-event-{i}", @event.Name);
            Assert.Equal($"test-event-description-{i}", @event.Description);
        }
    }

    private static async Task CreateTestData(INatsJSContext jetStream, CancellationToken token)
    {
        var stream = await jetStream.CreateStreamAsync(new StreamConfig(StreamName, [SubjectName]), cancellationToken: token);
        Assert.Equal(StreamName, stream.Info.Config.Name);

        for (var i = 0; i < 10; i++)
        {
            var appEvent = new AppEvent(SubjectName, $"test-event-{i}", $"test-event-description-{i}", i);
            var ack = await jetStream.PublishAsync(appEvent.Subject, appEvent, cancellationToken: token);
            ack.EnsureSuccess();
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnNatsBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddNats("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddNats("dependentresource")
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
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public Task Nats_WithPersistentLifetime_ReusesContainer()
    {
        return PersistentContainerTestHelpers.AssertResourceReusesContainerAsync(
            testOutputHelper,
            builder => builder.AddNats("resource").WithPersistentLifetime(),
            "resource",
            useTestContainerRegistry: true);
    }

}
