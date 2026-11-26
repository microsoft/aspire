// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;
using Xunit;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

namespace Aspire.Hosting.RemoteHost.Tests;

public class JsonRpcCallbackInvokerTests
{
    [Theory]
    [InlineData(250)]
    [InlineData(2000)]
    public async Task ExplicitFiniteTimeoutUsesOverrideRatherThanConfiguredDeadline(int millisecondsTimeout)
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
            ["IntegrationHost:CallbackTimeout"] = "00:00:01"
        }).Build());
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance, configuration, clock);
        invoker.SetConnection(connection.ServerRpc);
        var invocation = invoker.InvokeAsync<JsonNode?>("finite", null, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(millisecondsTimeout));
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(millisecondsTimeout - 1));
            Assert.False(invocation.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));

            var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation);
            Assert.Equal($"Callback 'finite' timed out after {millisecondsTimeout / 1000d}s.", error.Message);
            Assert.True(invoker.IsConnected);
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

#pragma warning disable ASPIRETESTING001 // Exercises the exported experimental testing callback.
    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    public async Task TestingCollectorUsesRunLifetimeRatherThanOrdinaryCallbackTimeout(string finish)
    {
        var clock = new FakeTimeProvider();
        using var runCancellation = new CancellationTokenSource();
        var resourceCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestRelease = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentQueue<TestResult>();
        var testingContext = new ResourceTestingContext(new TestingContext(AppContext.BaseDirectory),
            "tests", AppContext.BaseDirectory, results.Enqueue, resourceCompletion.Task, runCancellation.Token);
        using var connection = new IntegrationHostTestConnection(
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            async (callbackId, _, cancellationToken) =>
            {
                if (callbackId == "ordinary")
                {
                    ordinaryStarted.TrySetResult();
                    return await guestRelease.Task.WaitAsync(cancellationToken);
                }

                collectorStarted.TrySetResult();
                if (finish != "complete")
                {
                    // Model guest JavaScript that ignores JSON-RPC cancellation entirely.
                    return await guestRelease.Task;
                }

                Assert.True(await testingContext.WaitForCompletionAsync(cancellationToken));
                testingContext.PublishResult(new TestResult { Name = "remote-result", Outcome = TestOutcome.Passed });
                return null;
            });
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IntegrationHost:CallbackTimeout"] = "00:00:01"
        }).Build());
        await using var invoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance, configuration, clock);
        invoker.SetConnection(connection.ServerRpc);
        await using var handles = new HandleRegistry();
        using var tokens = new CancellationTokenRegistry();
        var assembly = typeof(TestResourceBuilderExtensions).Assembly;
        var ats = AtsCapabilityScanner.ScanAssemblies([assembly]).ToAtsContext();
        AtsCallbackProxyFactory? factory = null;
        var marshaller = new AtsMarshaller(handles, ats, tokens, new Lazy<AtsCallbackProxyFactory>(() => factory!));
        using var factoryLifetime = factory = new AtsCallbackProxyFactory(invoker, handles, tokens, marshaller);
        var dispatcher = new CapabilityDispatcher(handles, marshaller, [assembly]);
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddExecutable("tests", "unused", ".");
        var handle = handles.Register(resource, "Aspire.Hosting/Aspire.Hosting.ApplicationModel.IResourceBuilder");
        dispatcher.Invoke("Aspire.Hosting/withTesting", new JsonObject
        {
            ["builder"] = new JsonObject { ["$handle"] = handle },
            ["callback"] = "collector"
        });
        var collector = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Collector!;
        var invocation = collector(testingContext);
        var ordinary = Assert.IsType<Func<Task>>(factory.CreateProxy("ordinary", typeof(Func<Task>)))();
        try
        {
            await Task.WhenAll(collectorStarted.Task, ordinaryStarted.Task).WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<TimeoutException>(() => ordinary);
            Assert.False(invocation.IsCompleted);

            if (finish == "complete")
            {
                resourceCompletion.SetResult(true);
                await invocation.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(TestOutcome.Passed, Assert.Single(results).Outcome);
            }
            else if (finish == "cancel")
            {
                runCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    invocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.True(invoker.IsConnected);
                Assert.Throws<OperationCanceledException>(() =>
                    testingContext.PublishResult(new TestResult { Name = "late-result", Outcome = TestOutcome.Passed }));
            }
            else
            {
                connection.ServerRpc.Dispose();
                var error = await Record.ExceptionAsync(() =>
                    invocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.True(error is OperationCanceledException or ConnectionLostException, error?.ToString());
                // Disconnection can cancel callbacks before JSON-RPC publishes completion.
                await connection.ServerRpc.Completion.WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(invoker.LifetimeToken.IsCancellationRequested);
                Assert.False(invoker.IsConnected);
            }
        }
        finally
        {
            resourceCompletion.TrySetResult(false);
            guestRelease.TrySetResult(null);
            testingContext.Close();
        }
    }
#pragma warning restore ASPIRETESTING001

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
