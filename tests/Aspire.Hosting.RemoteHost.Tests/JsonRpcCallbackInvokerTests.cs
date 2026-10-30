// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class JsonRpcCallbackInvokerTests
{
    [Fact]
    public async Task ConfiguredCallbackDeadlineUsesTheInjectedClock()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            (_, _, _) =>
            {
                started.TrySetResult();
                return response.Task;
            });
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true",
            ["IntegrationHost:CallbackTimeout"] = "00:03:00"
        }).Build());
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance, configuration, clock);
        invoker.SetConnection(connection.ServerRpc);
        var invocation = invoker.InvokeAsync<JsonNode?>("stall", null, TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(179));
            Assert.False(invocation.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation);
            Assert.Equal("Callback 'stall' timed out after 180s; its owner connection was retired.", error.Message);
            Assert.True(invoker.LifetimeToken.IsCancellationRequested);
            await connection.ServerRpc.Completion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(invoker.IsConnected);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.InvokeAsync<JsonNode>(
                "responsive", null, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Fact]
    public async Task OrdinaryCallbackTimeoutLeavesConnectionUsable()
    {
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            async (callbackId, _, cancellationToken) =>
            {
                if (callbackId == "stall")
                {
                    return await response.Task.WaitAsync(cancellationToken);
                }

                return JsonValue.Create("responsive");
            });
        await using var invoker = new JsonRpcCallbackInvoker(
            NullLogger<JsonRpcCallbackInvoker>.Instance, IntegrationHostConfiguration.Default, new FakeTimeProvider());
        invoker.SetConnection(connection.ServerRpc);

        await Assert.ThrowsAsync<TimeoutException>(() => invoker.InvokeAsync<JsonNode>(
            "stall", null, TestContext.Current.CancellationToken, TimeSpan.Zero));

        Assert.True(invoker.IsConnected);
        var result = await invoker.InvokeAsync<JsonNode>(
            "responsive", null, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(10));
        Assert.Equal("responsive", result.GetValue<string>());
    }
}
