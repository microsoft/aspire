// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class ExternalCapabilityRegistryTests
{
    [Fact]
    public async Task TryInvokeAsync_DeferredGuestCallbackSurvivesExportReturn()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        string? relayId = null;
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            return Task.FromResult<JsonNode?>(null);
        });
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var owner = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);

        await registry.TryInvokeAsync("test.external/callback", new JsonObject { ["configure"] = "guest_callback" }, owner);

        Assert.NotNull(relayId);
        Assert.Equal((owner, "guest_callback"), registry.ResolveCallbackOwner(relayId));
        Assert.True(registry.MarkHostUnavailable(connection.ServerRpc));
        Assert.True(registry.MarkHostUnavailable(connection.ServerRpc));
        Assert.Null(registry.ResolveCallbackOwner(relayId));
    }

    [Fact]
    public async Task TryInvokeAsync_LiveHostTimesOutAndRetiresOutstandingWork()
    {
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var clock = new FakeTimeProvider();
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true",
            ["IntegrationHost:InvocationTimeout"] = "00:00:00.100",
            ["IntegrationHost:CallbackTimeout"] = "00:00:00.300"
        }).Build());
        using var registry = new ExternalCapabilityRegistry(
            loggerFactory.CreateLogger<ExternalCapabilityRegistry>(), configuration, clock, RemoteHostProfilingTelemetry.Disabled);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(CreateCapabilities("test.external/stall"), (_, _) =>
        {
            requestStarted.TrySetResult();
            return response.Task;
        });
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var invocation = registry.TryInvokeAsync("test.external/stall", null);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.StartsWith("Integration capability 'test.external/stall' (invocation ", error.Message);
            await connection.ServerRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TryInvokeAsync("test.external/stall", null));
            var records = sink.Writes.Where(write => write.State is IEnumerable<KeyValuePair<string, object?>> state &&
                state.Any(entry => entry.Key == "InvocationId")).ToArray();
            Assert.Collection(records,
                write => Assert.Equal(LogLevel.Information, write.LogLevel),
                write => Assert.IsType<TimeoutException>(write.Exception),
                write => Assert.Equal(LogLevel.Information, write.LogLevel));
            var states = records.Select(write => ((IEnumerable<KeyValuePair<string, object?>>)write.State!).ToDictionary()).ToArray();
            var invocationId = Assert.IsType<string>(states[0]["InvocationId"]);
            Assert.Equal("test.external/stall", states[0]["CapabilityId"]);
            Assert.All(states, state => Assert.Equal(invocationId, state["InvocationId"]));
            Assert.All(states, state => Assert.Equal(connection.ServerRpc.GetHashCode(), state["Host"]));
            Assert.Equal(TimeSpan.FromMilliseconds(100), states[1]["Timeout"]);
            Assert.Equal(TimeSpan.FromMilliseconds(100), states[1]["Elapsed"]);
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryInvokeAsync_NegotiatesInvocationIdsWithoutChangingCapabilitySignatures(bool currentProtocol)
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var payload = JsonNode.Parse(CreateCapabilities("test.external/value").GetRawText())!.AsObject();
        if (currentProtocol)
        {
            payload["protocolVersion"] = 2;
        }
        using var connection = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(payload),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("configured")));
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("configured", (await registry.TryInvokeAsync("test.external/value", null)).Result!.GetValue<string>());
        Assert.Equal(currentProtocol, Guid.TryParseExact(connection.LastInvocationId, "N", out _));
    }

    [Fact]
    public async Task InitializeAllHostsAsync_RejectsCapabilityWithoutReturnType()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var connection = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(new
        {
            capabilities = new[] { new { id = "test.external/missingReturnType", method = "missingReturnType" } }
        }));
        registry.AddIntegrationHost(connection.ServerRpc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InitializeAllHostsAsync(
            1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("test.external/missingReturnType", error.InnerException!.Message);
        Assert.Contains("returnType", error.InnerException.Message);
        Assert.False(registry.IsRegistered("test.external/missingReturnType"));
        Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
    }

    [Theory]
    [InlineData("id", false, null)]
    [InlineData("id", true, null)]
    [InlineData("id", true, "")]
    [InlineData("id", true, " \t\r\n")]
    [InlineData("method", false, null)]
    [InlineData("method", true, null)]
    [InlineData("method", true, "")]
    [InlineData("method", true, " \t\r\n")]
    public async Task InitializeAllHostsAsync_RejectsInvalidRequiredCapabilityFieldsWithoutPublishing(
        string fieldName, bool includeField, string? fieldValue)
    {
        var payload = JsonNode.Parse(CreateCapabilities("test.external/valid", "test.external/invalid").GetRawText())!.AsObject();
        var invalidCapability = payload["capabilities"]![1]!.AsObject();
        if (includeField)
        {
            invalidCapability[fieldName] = fieldValue;
        }
        else
        {
            invalidCapability.Remove(fieldName);
        }
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        using var registry = new ExternalCapabilityRegistry(loggerFactory.CreateLogger<ExternalCapabilityRegistry>());
        using var connection = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(payload));
        registry.AddIntegrationHost(connection.ServerRpc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InitializeAllHostsAsync(
            1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var validationError = Assert.IsType<JsonException>(error.InnerException);
        Assert.Equal(fieldName == "id"
            ? "Integration capabilities must declare a non-empty id."
            : "Integration capability 'test.external/invalid' must declare a non-empty method.", validationError.Message);
        var diagnostic = Assert.Single(sink.Writes, write => write.LogLevel == LogLevel.Error);
        Assert.Equal("Integration host capability discovery failed.", diagnostic.Message);
        Assert.Same(error, diagnostic.Exception);
        Assert.False(registry.IsRegistered("test.external/valid"));
        Assert.False(registry.IsRegistered("test.external/invalid"));
        Assert.False(registry.IsRegistered(""));
        var augmentationError = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
        Assert.Same(error, augmentationError.InnerException);
    }

    [Fact]
    public async Task InvokeGuestCallbackAsync_DeferredRelayUsesItsOriginalGuestAndRejectsOtherHosts()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        string? relayId = null;
        using var integration = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            return Task.FromResult<JsonNode?>(null);
        });
        using var guest = new IntegrationHostTestConnection(CreateCapabilities(), (id, args, _) =>
        {
            Assert.Equal("guest_callback", id);
            return Task.FromResult<JsonNode?>(JsonValue.Create($"deferred-{args!["p0"]!.GetValue<string>()}"));
        });
        registry.AddIntegrationHost(integration.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await registry.TryInvokeAsync("test.external/callback", new JsonObject { ["configure"] = "guest_callback" }, guest.CallbackInvoker);
        Assert.NotNull(relayId);

        var result = await registry.InvokeGuestCallbackAsync(integration.ServerRpc, relayId,
            new JsonObject { ["p0"] = "configured" }, TestContext.Current.CancellationToken);
        Assert.Equal("deferred-configured", result!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InvokeGuestCallbackAsync(
            guest.ServerRpc, relayId, null, TestContext.Current.CancellationToken));
        registry.RemoveGuestCallbacks(guest.CallbackInvoker);
        Assert.Null(registry.ResolveCallbackOwner(relayId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.InvokeGuestCallbackAsync(
            integration.ServerRpc, relayId, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.TryInvokeAsync(
            "test.external/callback", new JsonObject { ["configure"] = "late_callback" }, guest.CallbackInvoker));
    }

    [Fact]
    public async Task TryInvokeAsync_CancellationRetiresANonCooperativeHost()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var integration = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"), (_, _) =>
        {
            started.TrySetResult();
            return response.Task;
        });
        registry.AddIntegrationHost(integration.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var invocation = registry.TryInvokeAsync("test.external/value", null, cancellationToken: cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
            await integration.ServerRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Fact]
    public async Task Dispatcher_GuestProjectedCancellationRetiresANonCooperativeHost()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var payload = JsonNode.Parse(CreateCapabilities("test.external/cancel").GetRawText())!.AsObject();
        payload["capabilities"]![0]!["parameters"] = JsonSerializer.SerializeToNode(new[]
        {
            new { name = "cancellationToken", type = new { typeId = "cancellationToken", category = "Primitive" } }
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var integration = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(payload), (_, _) =>
        {
            started.TrySetResult();
            return response.Task;
        });
        registry.AddIntegrationHost(integration.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await using var handles = new HandleRegistry();
        using var tokens = new CancellationTokenRegistry();
        var marshaller = new AtsMarshaller(handles, CreateContext(), tokens,
            new Lazy<AtsCallbackProxyFactory>(() => throw new NotImplementedException()));
        var configuration = new ConfigurationBuilder().Build();
        using var telemetry = new RemoteHostProfilingTelemetry(configuration);
        var dispatcher = new CapabilityDispatcher(handles,
            new AssemblyLoader(configuration, NullLogger<AssemblyLoader>.Instance, telemetry),
            marshaller, registry, NullLogger<CapabilityDispatcher>.Instance, telemetry);
        var invocation = dispatcher.InvokeAsync("test.external/cancel",
            new JsonObject { ["cancellationToken"] = "ct_guest" }, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(tokens.Cancel("ct_guest"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
            await integration.ServerRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Fact]
    public async Task InvokeGuestCallbackAsync_ConfiguredCallbackCanOutliveInvocationDeadline()
    {
        var clock = new FakeTimeProvider();
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true",
            ["IntegrationHost:InvocationTimeout"] = "00:00:00.100",
            ["IntegrationHost:CallbackTimeout"] = "00:00:00.300"
        }).Build());
        using var registry = new ExternalCapabilityRegistry(
            NullLogger<ExternalCapabilityRegistry>.Instance, configuration, clock, RemoteHostProfilingTelemetry.Disabled);
        string? relayId = null;
        using var integration = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            return Task.FromResult<JsonNode?>(null);
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guest = new IntegrationHostTestConnection(CreateCapabilities(), (_, _, _) =>
        {
            started.TrySetResult();
            return response.Task;
        });
        await using var guestInvoker = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance, configuration, clock);
        guestInvoker.SetConnection(guest.ServerRpc);
        registry.AddIntegrationHost(integration.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await registry.TryInvokeAsync("test.external/callback", new JsonObject { ["configure"] = "guest_callback" }, guestInvoker);
        Assert.NotNull(relayId);
        var invocation = registry.InvokeGuestCallbackAsync(integration.ServerRpc, relayId, null, TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.False(invocation.IsCompleted);
            response.SetResult(JsonValue.Create("completed"));
            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("completed", result!.GetValue<string>());
            Assert.True(guestInvoker.IsConnected);
            Assert.False(guestInvoker.LifetimeToken.IsCancellationRequested);
            Assert.True((await registry.TryInvokeAsync(
                "test.external/callback", new JsonObject { ["configure"] = "guest_callback" }, guestInvoker)).Found);
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 300)]
    [InlineData(300, 100)]
    public async Task InvokeGuestCallbackAsync_StalledRelayRetiresBothConnectionsAndRequiresSessionRestart(
        int invocationMilliseconds, int callbackMilliseconds)
    {
        var clock = new FakeTimeProvider();
        var configuration = new IntegrationHostConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true",
            ["IntegrationHost:InvocationTimeout"] = TimeSpan.FromMilliseconds(invocationMilliseconds).ToString("c"),
            ["IntegrationHost:CallbackTimeout"] = TimeSpan.FromMilliseconds(callbackMilliseconds).ToString("c")
        }).Build());
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        using var registry = new ExternalCapabilityRegistry(
            loggerFactory.CreateLogger<ExternalCapabilityRegistry>(), configuration, clock, RemoteHostProfilingTelemetry.Disabled);
        string? relayId = null;
        using var integration = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            return Task.FromResult<JsonNode?>(null);
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var guest = new IntegrationHostTestConnection(CreateCapabilities(), (_, _, _) =>
        {
            started.TrySetResult();
            return response.Task;
        });
        await using var guestInvoker = new JsonRpcCallbackInvoker(
            NullLogger<JsonRpcCallbackInvoker>.Instance, configuration, clock);
        guestInvoker.SetConnection(guest.ServerRpc);
        _ = registry.ExpectHostRegistration("integration");
        registry.AddIntegrationHost("integration", integration.ServerRpc, integration.CallbackInvoker);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await registry.TryInvokeAsync("test.external/callback", new JsonObject { ["configure"] = "guest_callback" }, guestInvoker);
        Assert.NotNull(relayId);
        try
        {
            var invocation = registry.InvokeGuestCallbackAsync(integration.ServerRpc, relayId, null, TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(callbackMilliseconds - 1));
            Assert.False(invocation.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
            var records = sink.Writes.Where(write => write.State is IEnumerable<KeyValuePair<string, object?>> state &&
                state.Any(entry => entry.Key == "CallbackId") &&
                state.Any(entry => entry.Key is "Timeout" or "Elapsed")).ToArray();
            Assert.Collection(records,
                write => Assert.Equal(LogLevel.Information, write.LogLevel),
                write =>
                {
                    Assert.Equal(LogLevel.Error, write.LogLevel);
                    Assert.IsType<TimeoutException>(write.Exception);
                },
                write => Assert.Equal(LogLevel.Information, write.LogLevel));
            var states = records.Select(write => ((IEnumerable<KeyValuePair<string, object?>>)write.State!).ToDictionary()).ToArray();
            var invocationId = Assert.IsType<string>(states[0]["InvocationId"]);
            Assert.All(states, state => Assert.Equal(invocationId, state["InvocationId"]));
            Assert.Equal(configuration.CallbackTimeout, states[0]["Timeout"]);
            Assert.Equal(configuration.CallbackTimeout, states[1]["Timeout"]);
            Assert.Equal(configuration.CallbackTimeout, states[1]["Elapsed"]);
            Assert.Equal(
                $"Guest callback relay '{relayId}' for integration capability 'test.external/callback' " +
                $"(invocation {invocationId}) timed out after {configuration.CallbackTimeout}. Restart the AppHost session to rebuild callbacks.",
                error.Message);
            await integration.ServerRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await guest.ServerRpc.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(registry.ResolveCallbackOwner(relayId));
            Assert.True(registry.MarkHostUnavailable(integration.ServerRpc));
            using var replacement = new IntegrationHostTestConnection(CreateCallbackCapabilities());
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceHostAsync(
                integration.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Fact]
    public async Task Registration_RequiresTheExpectedAttemptAndRejectsDuplicates()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var connection = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var registration = registry.ExpectHostRegistration("attempt");

        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("unknown", connection.ServerRpc, connection.CallbackInvoker));
        Assert.False(registration.IsCompleted);
        registry.AddIntegrationHost("attempt", connection.ServerRpc, connection.CallbackInvoker);
        Assert.Same(connection.ServerRpc, await registration);
        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("attempt", connection.ServerRpc, connection.CallbackInvoker));
        Assert.Equal(1, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(0, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registration_ExpiredAttemptCannotRegisterForItsReplacement()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var connection = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        _ = registry.ExpectHostRegistration("old");
        registry.ForgetHostRegistration("old");
        var replacement = registry.ExpectHostRegistration("replacement");

        Assert.Throws<InvalidOperationException>(() => registry.AddIntegrationHost("old", connection.ServerRpc, connection.CallbackInvoker));
        Assert.False(replacement.IsCompleted);
        registry.AddIntegrationHost("replacement", connection.ServerRpc, connection.CallbackInvoker);
        Assert.Same(connection.ServerRpc, await replacement);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceHostAsync_RequiresSessionRestartForCallbackOwners(bool ownsCallbacks)
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var registration = registry.ExpectHostRegistration("original");
        registry.AddIntegrationHost("original", original.ServerRpc, original.CallbackInvoker);
        Assert.Same(original.ServerRpc, await registration);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (ownsCallbacks)
        {
            original.CallbackInvoker.RegisterCallback();
        }

        Assert.Equal(ownsCallbacks, registry.MarkHostUnavailable(original.ServerRpc));
        Assert.Equal(ownsCallbacks, registry.MarkHostUnavailable(original.ServerRpc));
        Assert.Throws<ObjectDisposedException>(original.CallbackInvoker.RegisterCallback);
        using var replacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        registry.AddIntegrationHost(replacement.ServerRpc);
        if (ownsCallbacks)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceHostAsync(
                original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(
                "The integration host contributed callbacks to the resource model. " +
                "Restart the AppHost session to rebuild those callbacks before using the integration.",
                error.Message);
            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TryInvokeAsync("test.external/value", null));
            Assert.Equal(
                "The integration host providing 'test.external/value' contributed callbacks to the resource model. " +
                "Restart the AppHost session to rebuild those callbacks before using the integration.",
                unavailable.Message);
        }
        else
        {
            await registry.ReplaceHostAsync(
                original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal("test.external/value", Assert.Single(registry.AugmentContext(CreateContext()).Capabilities).CapabilityId);
    }

    [Fact]
    public async Task ReplaceHostAsync_RediscoveryRoutesOnlyTheRecoveredHostsCapabilities()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("original")));
        using var other = new IntegrationHostTestConnection(CreateCapabilities("test.external/other"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("other")));
        using var replacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("replacement")));
        registry.AddIntegrationHost(original.ServerRpc);
        registry.AddIntegrationHost(other.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);

        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.TryInvokeAsync("test.external/value", null));
        Assert.Equal("The integration host providing 'test.external/value' is restarting. Retry after it has registered again.", unavailable.Message);
        registry.AddIntegrationHost(replacement.ServerRpc);
        await registry.ReplaceHostAsync(original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("replacement", (await registry.TryInvokeAsync("test.external/value", null)).Result!.GetValue<string>());
        Assert.Equal("other", (await registry.TryInvokeAsync("test.external/other", null)).Result!.GetValue<string>());
        Assert.Collection(registry.AugmentContext(CreateContext()).Capabilities,
            cap => Assert.Equal("test.external/other", cap.CapabilityId),
            cap => Assert.Equal("test.external/value", cap.CapabilityId));
    }

    [Theory]
    [InlineData("test.external/different", "externalMethod", "string")]
    [InlineData("test.external/value", "differentMethod", "string")]
    [InlineData("test.external/value", "externalMethod", "int")]
    public async Task ReplaceHostAsync_RejectsChangedProjectionWithoutPublishing(string id, string method, string typeId)
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        using var replacement = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(new
        {
            capabilities = new[] { new { id, method, returnType = new { typeId, category = "Primitive" } } }
        }));
        registry.AddIntegrationHost(original.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);
        registry.AddIntegrationHost(replacement.ServerRpc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReplaceHostAsync(
            original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.StartsWith("The restarted integration host changed its capability signatures.", error.Message);
        var capability = Assert.Single(registry.AugmentContext(CreateContext()).Capabilities);
        Assert.Equal("test.external/value", capability.CapabilityId);
        Assert.Equal("externalMethod", capability.MethodName);
        Assert.Equal("string", capability.ReturnType!.TypeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TryInvokeAsync("test.external/value", null));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("method")]
    public async Task ReplaceHostAsync_RejectsMissingRequiredCapabilityFieldsWithoutPublishing(string fieldName)
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var payload = JsonNode.Parse(CreateCapabilities("test.external/value").GetRawText())!.AsObject();
        payload["capabilities"]![0]!.AsObject().Remove(fieldName);
        using var replacement = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(payload));
        registry.AddIntegrationHost(original.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);
        registry.AddIntegrationHost(replacement.ServerRpc);

        var error = await Assert.ThrowsAsync<JsonException>(() => registry.ReplaceHostAsync(
            original.ServerRpc, replacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(fieldName == "id"
            ? "Integration capabilities must declare a non-empty id."
            : "Integration capability 'test.external/value' must declare a non-empty method.", error.Message);
        var capability = Assert.Single(registry.AugmentContext(CreateContext()).Capabilities);
        Assert.Equal("test.external/value", capability.CapabilityId);
        Assert.Equal("externalMethod", capability.MethodName);
        Assert.Equal("string", capability.ReturnType!.TypeId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.TryInvokeAsync("test.external/value", null));
    }

    [Fact]
    public async Task ReplaceHostAsync_TimesOutWithoutChangingThePublishedMetadata()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var original = new IntegrationHostTestConnection(CreateCapabilities("test.external/value"));
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var replacement = new IntegrationHostTestConnection(_ => response.Task);
        registry.AddIntegrationHost(original.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(original.ServerRpc);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => registry.ReplaceHostAsync(
                original.ServerRpc, replacement.ServerRpc, TimeSpan.Zero, TestContext.Current.CancellationToken));
            Assert.Equal("test.external/value", Assert.Single(registry.AugmentContext(CreateContext()).Capabilities).CapabilityId);
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/value"));
        }
    }

    [Fact]
    public async Task ReplaceHostAsync_ConcurrentRecoveriesPreserveBothHosts()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        using var first = new IntegrationHostTestConnection(CreateCapabilities("test.external/first"));
        using var second = new IntegrationHostTestConnection(CreateCapabilities("test.external/second"));
        using var firstReplacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/first"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("first-replacement")));
        using var secondReplacement = new IntegrationHostTestConnection(CreateCapabilities("test.external/second"),
            (_, _) => Task.FromResult<JsonNode?>(JsonValue.Create("second-replacement")));
        registry.AddIntegrationHost(first.ServerRpc);
        registry.AddIntegrationHost(second.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.MarkHostUnavailable(first.ServerRpc);
        registry.MarkHostUnavailable(second.ServerRpc);
        registry.AddIntegrationHost(firstReplacement.ServerRpc);
        registry.AddIntegrationHost(secondReplacement.ServerRpc);

        await Task.WhenAll(
            registry.ReplaceHostAsync(first.ServerRpc, firstReplacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            registry.ReplaceHostAsync(second.ServerRpc, secondReplacement.ServerRpc, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal("first-replacement", (await registry.TryInvokeAsync("test.external/first", null)).Result!.GetValue<string>());
        Assert.Equal("second-replacement", (await registry.TryInvokeAsync("test.external/second", null)).Result!.GetValue<string>());
    }

    [Fact]
    public async Task Stop_CancelsPendingInvocationAndRemovesItsCallbackOwner()
    {
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            request.TrySetResult(args!["configure"]!.GetValue<string>());
            return response.Task;
        });
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var invocation = registry.TryInvokeAsync("test.external/callback",
            new JsonObject { ["configure"] = "guest_callback" }, new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance));
        try
        {
            var relayId = await request.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            registry.Stop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                invocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Null(registry.ResolveCallbackOwner(relayId));
        }
        finally
        {
            response.TrySetResult(null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryInvokeAsync_ReusedCallbackIdsHaveIndependentOwners(bool sameGuest)
    {
        var firstRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            var isFirst = args!["name"]!.GetValue<string>() == "first";
            (isFirst ? firstRequest : secondRequest).TrySetResult(args["configure"]!.GetValue<string>());
            return (isFirst ? firstResponse : secondResponse).Task;
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var firstOwner = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);
        using var secondOwner = sameGuest ? firstOwner : new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);
        var firstArgs = new JsonObject { ["name"] = "first", ["configure"] = "guest_callback" };
        var secondArgs = new JsonObject { ["name"] = "second", ["configure"] = "guest_callback" };

        try
        {
            var firstInvocation = registry.TryInvokeAsync("test.external/callback", firstArgs, firstOwner);
            var firstRelayId = await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var secondInvocation = registry.TryInvokeAsync("test.external/callback", secondArgs, secondOwner);
            var secondRelayId = await secondRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotEqual(firstRelayId, secondRelayId);
            Assert.Equal("guest_callback", firstArgs["configure"]!.GetValue<string>());
            Assert.Equal("guest_callback", secondArgs["configure"]!.GetValue<string>());
            Assert.Equal((firstOwner, "guest_callback"), registry.ResolveCallbackOwner(firstRelayId));
            Assert.Equal((secondOwner, "guest_callback"), registry.ResolveCallbackOwner(secondRelayId));

            firstResponse.SetResult(null);
            Assert.True((await firstInvocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Found);
            Assert.Equal((firstOwner, "guest_callback"), registry.ResolveCallbackOwner(firstRelayId));
            Assert.Equal((secondOwner, "guest_callback"), registry.ResolveCallbackOwner(secondRelayId));

            secondResponse.SetResult(null);
            Assert.True((await secondInvocation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Found);
            Assert.Equal((secondOwner, "guest_callback"), registry.ResolveCallbackOwner(secondRelayId));
            registry.RemoveGuestCallbacks(firstOwner);
            registry.RemoveGuestCallbacks(secondOwner);
            Assert.Null(registry.ResolveCallbackOwner(firstRelayId));
            Assert.Null(registry.ResolveCallbackOwner(secondRelayId));
        }
        finally
        {
            firstResponse.TrySetResult(null);
            secondResponse.TrySetResult(null);
        }
    }

    [Fact]
    public async Task TryInvokeAsync_FailedInvocationRetainsCallbacksUntilOwnerDisconnects()
    {
        string? relayId = null;
        using var connection = new IntegrationHostTestConnection(CreateCallbackCapabilities(), (_, args) =>
        {
            relayId = args!["configure"]!.GetValue<string>();
            throw new InvalidOperationException("Integration callback failed.");
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var owner = new JsonRpcCallbackInvoker(NullLogger<JsonRpcCallbackInvoker>.Instance);
        await Assert.ThrowsAsync<RemoteInvocationException>(() => registry.TryInvokeAsync(
            "test.external/callback",
            new JsonObject { ["configure"] = "guest_callback" },
            owner).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.NotNull(relayId);
        Assert.Equal((owner, "guest_callback"), registry.ResolveCallbackOwner(relayId));
        registry.RemoveGuestCallbacks(owner);
        Assert.Null(registry.ResolveCallbackOwner(relayId));
    }

    [Fact]
    public async Task InitializeAllHostsAsync_CancellationStopsPendingDiscovery()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(_ =>
        {
            requestStarted.TrySetResult();
            // Simulate a registered host that never responds, even to RPC cancellation.
            return response.Task;
        });
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var initialization = registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(initialization.IsCompleted);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                initialization.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(registry.IsRegistered("test.external/pending"));
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/pending"));
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_TimesOutPendingDiscovery()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new IntegrationHostTestConnection(_ => response.Task);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        Assert.Equal(1, await registry.WaitForHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken));

        try
        {
            // An already-expired timeout makes this deterministic without a clock or
            // sleeps. The RPC response cannot win the race because its task stays pending.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.InitializeAllHostsAsync(1, TimeSpan.Zero, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.False(registry.IsRegistered("test.external/pending"));
            var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
            Assert.Same(exception, augmentationException.InnerException);
        }
        finally
        {
            response.TrySetResult(CreateCapabilities("test.external/pending"));
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_RejectsMalformedCapabilityPayloads()
    {
        foreach (var json in new[] { "null", "{}", "{\"capabilities\":null}", "{\"capabilities\":{}}", "{\"capabilities\":42}" })
        {
            using var payload = JsonDocument.Parse(json);
            using var connection = new IntegrationHostTestConnection(payload.RootElement);
            using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
            registry.AddIntegrationHost(connection.ServerRpc);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.IsType<JsonException>(exception.InnerException);
            var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
            Assert.Same(exception, augmentationException.InnerException);
        }
    }

    [Fact]
    public async Task InitializeAllHostsAsync_FailedDiscoveryDoesNotExposePartialContext()
    {
        using var connection = new IntegrationHostTestConnection(_ =>
            Task.FromException<JsonElement>(new InvalidOperationException("Host discovery failed.")));
        using var successfulConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/partial"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        registry.AddIntegrationHost(successfulConnection.ServerRpc);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.IsType<RemoteInvocationException>(exception.InnerException);
        Assert.False(registry.IsRegistered("test.external/partial"));
        var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
        Assert.Same(exception, augmentationException.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAllHostsAsync_RejectsDuplicateExternalIdsWithoutPublishing(bool differentHosts)
    {
        using var firstConnection = new IntegrationHostTestConnection(differentHosts
            ? CreateCapabilities("test.external/first", "test.external/shared")
            : CreateCapabilities("test.external/first", "test.external/shared", "test.external/second", "test.external/shared"));
        using var secondConnection = new IntegrationHostTestConnection(
            CreateCapabilities("test.external/second", "test.external/shared"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        if (differentHosts)
        {
            registry.AddIntegrationHost(secondConnection.ServerRpc);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(differentHosts ? 2 : 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Contains("Capability ID 'test.external/shared' is provided by multiple external registrations", exception.Message);
        Assert.False(registry.IsRegistered("test.external/first"));
        Assert.False(registry.IsRegistered("test.external/second"));
        Assert.False(registry.IsRegistered("test.external/shared"));

        var augmentationException = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(CreateContext()));
        Assert.Equal("Integration host capability discovery failed.", augmentationException.Message);
        Assert.Same(exception, augmentationException.InnerException);
    }

    [Fact]
    public async Task InitializeAllHostsAsync_CollisionPreservesPreviousRegistrations()
    {
        using var firstConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/existing"));
        using var secondConnection = new IntegrationHostTestConnection(
            CreateCapabilities("test.external/new", "test.external/existing"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        registry.AddIntegrationHost(secondConnection.ServerRpc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.True(registry.IsRegistered("test.external/existing"));
        Assert.False(registry.IsRegistered("test.external/new"));
    }

    [Fact]
    public async Task AugmentContext_RejectsManagedExternalCollision()
    {
        var payload = CreateCapabilities("test.managed/shared");
        using var connection = new IntegrationHostTestConnection(payload);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var managedCapability = new AtsCapabilityInfo
        {
            CapabilityId = "test.managed/shared",
            MethodName = "managedMethod",
            Parameters = [],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Boolean, Category = AtsTypeCategory.Primitive }
        };
        var context = CreateContext(managedCapability);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.AugmentContext(context));

        Assert.Equal(
            "Capability ID 'test.managed/shared' is provided by both a managed capability and an external integration host. Capability IDs must be unique.",
            exception.Message);
        Assert.Same(managedCapability, Assert.Single(context.Capabilities));
    }

    [Fact]
    public async Task AugmentContext_PreservesManagedOrderAndSortsExternalCapabilities()
    {
        using var firstConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/z", "test.external/a"));
        using var secondConnection = new IntegrationHostTestConnection(CreateCapabilities("test.external/m"));
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        registry.AddIntegrationHost(secondConnection.ServerRpc);
        await registry.InitializeAllHostsAsync(2, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var managedCapability = new AtsCapabilityInfo
        {
            CapabilityId = "test.managed/z",
            MethodName = "managedMethod",
            Parameters = [],
            ReturnType = new AtsTypeRef { TypeId = AtsConstants.Boolean, Category = AtsTypeCategory.Primitive }
        };
        var context = CreateContext(managedCapability);

        var augmented = registry.AugmentContext(context);

        Assert.Collection(augmented.Capabilities,
            capability => Assert.Same(managedCapability, capability),
            capability => Assert.Equal("test.external/a", capability.CapabilityId),
            capability => Assert.Equal("test.external/m", capability.CapabilityId),
            capability => Assert.Equal("test.external/z", capability.CapabilityId));
    }

    [Fact]
    public async Task AugmentContext_PreservesManagedMetadataAndExternalNullability()
    {
        using var payload = JsonDocument.Parse("""
            {
              "capabilities": [
                {
                  "id": "test.external/readValue",
                  "method": "readValue",
                  "returnType": { "typeId": "string", "category": "Primitive", "isNullable": true }
                }
              ]
            }
            """);
        using var connection = new IntegrationHostTestConnection(payload.RootElement);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync(1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var context = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "test.managed",
                    PathSegments = ["Defaults", "Name"],
                    Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive },
                    Value = JsonValue.Create("managed-value")
                }
            ]
        };
        context.Methods["test.managed/toString"] = typeof(string).GetMethod(nameof(string.ToString), Type.EmptyTypes)!;
        context.Properties["test.managed/length"] = typeof(string).GetProperty(nameof(string.Length))!;

        var augmented = registry.AugmentContext(context);

        var capability = Assert.Single(augmented.Capabilities);
        Assert.Equal("test.external/readValue", capability.CapabilityId);
        Assert.Equal("readValue", capability.MethodName);
        Assert.True(capability.ReturnType!.IsNullable);
        Assert.Same(context.ExportedValues, augmented.ExportedValues);
        Assert.Same(context.HandleTypes, augmented.HandleTypes);
        Assert.Same(context.DtoTypes, augmented.DtoTypes);
        Assert.Same(context.EnumTypes, augmented.EnumTypes);
        Assert.Same(context.Diagnostics, augmented.Diagnostics);
        Assert.Equal(context.Methods, augmented.Methods);
        Assert.Equal(context.Properties, augmented.Properties);
        Assert.Empty(context.Capabilities);
    }

    private static JsonElement CreateCapabilities(params string[] ids)
        => JsonSerializer.SerializeToElement(new
        {
            capabilities = ids.Select(id => new
            {
                id,
                method = "externalMethod",
                returnType = new { typeId = "string", category = "Primitive" }
            }).ToArray()
        });

    private static JsonElement CreateCallbackCapabilities()
        => JsonSerializer.SerializeToElement(new
        {
            capabilities = new[]
            {
                new
                {
                    id = "test.external/callback",
                    method = "externalCallback",
                    parameters = new[] { new { name = "configure", isCallback = true } },
                    returnType = new { typeId = "void", category = "Primitive" }
                }
            }
        });

    private static AtsContext CreateContext(params AtsCapabilityInfo[] capabilities)
        => new()
        {
            Capabilities = capabilities,
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = []
        };
}
