// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnRabbitMQBlocksDependentResources()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddRabbitMQ("resource")
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddRabbitMQ("dependentresource")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync();

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await pendingStart.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyRabbitMQResource()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var rabbitMQ = builder.AddRabbitMQ("rabbitMQ");

        using var app = builder.Build();

        await app.StartAsync();

        var hb = Host.CreateApplicationBuilder();
        hb.AddTestLogging(testOutputHelper);
        hb.Configuration[$"ConnectionStrings:{rabbitMQ.Resource.Name}"] = await rabbitMQ.Resource.ConnectionStringExpression.GetValueAsync(default);
        hb.AddRabbitMQClient(rabbitMQ.Resource.Name);

        using var host = hb.Build();

        await host.StartAsync();

        var connection = host.Services.GetRequiredService<IConnection>();

        await using var channel = await connection.CreateChannelAsync();
        const string queueName = "hello";
        await channel.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

        const string message = "Hello World!";
        var body = Encoding.UTF8.GetBytes(message);

        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: queueName, body: body);

        var result = await channel.BasicGetAsync(queueName, true);
        Assert.Equal(message, Encoding.UTF8.GetString(result!.Body.Span));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task WithDataShouldPersistStateBetweenUsages(bool useVolume)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        string? volumeName = null;
        string? bindMountPath = null;

        try
        {
            using var builder1 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var rabbitMQ1 = builder1.AddRabbitMQ("rabbitMQ");
#pragma warning disable CS0618 // Type or member is obsolete
            var password = rabbitMQ1.Resource.PasswordParameter.Value;
#pragma warning restore CS0618 // Type or member is obsolete

            if (useVolume)
            {
                // Use a deterministic volume name to prevent them from exhausting the machines if deletion fails
                volumeName = VolumeNameGenerator.Generate(rabbitMQ1, nameof(WithDataShouldPersistStateBetweenUsages));

                // if the volume already exists (because of a crashing previous run), delete it
                DockerUtils.AttemptDeleteDockerVolume(volumeName, throwOnFailure: true);
                rabbitMQ1.WithDataVolume(volumeName);
            }
            else
            {
                bindMountPath = Directory.CreateTempSubdirectory().FullName;

                rabbitMQ1.WithDataBindMount(bindMountPath);
            }

            using (var app = builder1.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);
                    hb.Configuration[$"ConnectionStrings:{rabbitMQ1.Resource.Name}"] = await rabbitMQ1.Resource.ConnectionStringExpression.GetValueAsync(default);
                    hb.AddRabbitMQClient(rabbitMQ1.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();
                        await app.WaitForHealthyAsync(rabbitMQ1).WaitAsync(TestConstants.LongTimeoutTimeSpan);

                        var connection = host.Services.GetRequiredService<IConnection>();

                        await using var channel = await connection.CreateChannelAsync();
                        const string queueName = "hello";
                        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false);

                        const string message = "Hello World!";
                        var body = Encoding.UTF8.GetBytes(message);

                        var props = new BasicProperties();
                        props.Persistent = true; // or props.DeliveryMode = 2;
                        await channel.BasicPublishAsync(
                            exchange: string.Empty,
                            queueName,
                            mandatory: true,
                            props,
                            body);
                    }
                }
                finally
                {
                    // Stops the container, or the Volume/mount would still be in use
                    await app.StopAsync();
                }
            }

            testOutputHelper.WriteLine($"Starting the second run with the same volume/mount");

            using var builder2 = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
            var passwordParameter2 = builder2.AddParameter("pwd", password);

            var rabbitMQ2 = builder2.AddRabbitMQ("rabbitMQ", password: passwordParameter2);

            if (useVolume)
            {
                rabbitMQ2.WithDataVolume(volumeName);
            }
            else
            {
                rabbitMQ2.WithDataBindMount(bindMountPath!);
            }

            using (var app = builder2.Build())
            {
                await app.StartAsync();
                try
                {
                    var hb = Host.CreateApplicationBuilder();
                    hb.AddTestLogging(testOutputHelper);
                    hb.Configuration[$"ConnectionStrings:{rabbitMQ2.Resource.Name}"] = await rabbitMQ2.Resource.ConnectionStringExpression.GetValueAsync(default);
                    hb.AddRabbitMQClient(rabbitMQ2.Resource.Name);

                    using (var host = hb.Build())
                    {
                        await host.StartAsync();
                        await app.WaitForHealthyAsync(rabbitMQ2).WaitAsync(TestConstants.LongTimeoutTimeSpan);

                        var connection = host.Services.GetRequiredService<IConnection>();

                        await using var channel = await connection.CreateChannelAsync();
                        const string queueName = "hello";
                        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false);

                        var result = await channel.BasicGetAsync(queueName, true);
                        Assert.Equal("Hello World!", Encoding.UTF8.GetString(result!.Body.Span));
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
}
