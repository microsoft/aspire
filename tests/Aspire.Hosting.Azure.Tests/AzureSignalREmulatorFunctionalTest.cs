// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Azure.SignalR.Common;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;

namespace Aspire.Hosting.Azure.Tests;
public class AzureSignalREmulatorFunctionalTest(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task VerifyAzureSignalRConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var signalR = builder.AddAzureSignalR("signalr").RunAsEmulator(e =>
        {
            e.WithEndpoint("emulator", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10001));
        });

        Assert.True(signalR.Resource.IsEmulator);

        var connectionStringExpr = signalR.Resource.ConnectionStringExpression;
        var connectionString = await connectionStringExpr.GetValueAsync(CancellationToken.None);
        var postfix = ";AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;";
        Assert.Equal("Endpoint={signalr.bindings.emulator.url}" + postfix, connectionStringExpr.ValueExpression);
        Assert.Equal("Endpoint=http://localhost:10001" + postfix, connectionString);
        Assert.Equal(connectionString, await ((IResourceWithConnectionString)signalR.Resource).GetConnectionStringAsync());
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWaitForOnAzureSignalREmulatorBlocksDependentResources()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var signalR = builder.AddAzureSignalR("resource")
                            .RunAsEmulator()
                            .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
            .WaitFor(signalR);
        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync(signalR.Resource.Name, KnownResourceStates.Running, cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(signalR.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyAzureSignalREmulatorResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 10,
                Delay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Linear,
                ShouldHandle = new PredicateBuilder().Handle<AzureSignalRException>()
            })
            .Build();
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        var signalR = builder
            .AddAzureSignalR("signalR")
            .RunAsEmulator();

        using var app = builder.Build();
        await app.StartAsync();

        await pipeline.ExecuteAsync(async token =>
        {
            var connectionString = await signalR.Resource.ConnectionStringExpression.GetValueAsync(default);
            var serviceManager = new ServiceManagerBuilder()
                .WithOptions(option => { option.ConnectionString = connectionString; })
                .BuildServiceManager();
            Assert.True(await serviceManager.IsServiceHealthy(default));

            // Get negotiate URL to init a signalR connection
            var serviceHubContext = await serviceManager.CreateHubContextAsync("hub1", default);
            var negotiationResponse = await serviceHubContext.NegotiateAsync(new() { UserId = "testId" }, token);
            var connection = new HubConnectionBuilder().WithUrl(negotiationResponse.Url ?? "", option =>
            {
                option.AccessTokenProvider = () => Task.FromResult(negotiationResponse.AccessToken);
            }).Build();
            var messageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.On<string>("broadcast", message =>
            {
                messageTcs.TrySetResult(message);
            });
            await connection.StartAsync(token);

            // Broadcast message to all clients
            var sentMessage = "Hello, World!";
            await serviceHubContext.Clients.All.SendAsync("broadcast", sentMessage, token);

            // Verify that received message is the same as sent message
            Assert.Equal(sentMessage, await messageTcs.Task);
        }, cts.Token);

        await app.StopAsync();
    }
}
