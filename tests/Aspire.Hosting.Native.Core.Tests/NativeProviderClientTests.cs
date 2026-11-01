// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using NativeIntegration.Generated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderClientTests
{
    [Fact]
    public async Task ReplacementProviderReusesCapabilitiesAndTypesWithoutRevivingDisconnectedHandles()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        ExternalApiResourceClient? previous = null;
        for (var index = 0; index < 3; index++)
        {
            await using var provider = await ConnectAsync(host);
            var authority = await app.ExpectIntegrationHostAsync("example.csharp.external-api", TestContext.Current.CancellationToken);
            await provider.RegisterIntegrationHostAsync(authority, ProviderContract.Create(), TestContext.Current.CancellationToken);
            Assert.True(await app.WaitIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
            Assert.Equal(3, (await app.GetIntegrationCapabilitiesAsync(TestContext.Current.CancellationToken))
                .GetProperty("capabilities").GetArrayLength());
            if (previous is not null)
            {
                var stale = await Assert.ThrowsAsync<NativeCapabilityException>(() => previous.GetResourceNameAsync());
                Assert.Equal("HANDLE_NOT_FOUND", stale.Code);
            }
            previous = await composition.AddExternalApiAsync($"catalog{index}", "https://catalog.example/");
            Assert.Equal($"catalog{index}", await previous.GetResourceNameAsync());
            await provider.DisposeAsync();
            await Assert.ThrowsAsync<NativeCapabilityException>(() => previous.GetResourceNameAsync());
            Assert.Equal(0, (await app.GetIntegrationCapabilitiesAsync(TestContext.Current.CancellationToken))
                .GetProperty("capabilities").GetArrayLength());
            Assert.Equal(index + 1, (await composition.InspectAsync()).Resources.Length);
        }
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task AbandonedDiscoveryCannotPublishProviderCapabilitiesAfterItsReplyArrives()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        using var provider = new NativeSocketTestClient(host.Endpoint);
        var cancellationToken = TestContext.Current.CancellationToken;
        await provider.CallAsync("authenticate", new JsonArray(host.Token), cancellationToken);
        var authority = await app.ExpectIntegrationHostAsync("example.pending", cancellationToken);
        await provider.SendAsync("registerAsIntegrationHost", new JsonArray(authority), cancellationToken);
        var discovery = JsonNode.Parse((await provider.ReadAsync(cancellationToken))!)!.AsObject();
        Assert.Equal("getCapabilities", discovery["method"]!.GetValue<string>());
        Assert.True(await app.AbandonIntegrationHostRegistrationAsync(authority, cancellationToken));
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = discovery["id"]!.DeepClone(),
            ["result"] = JsonNode.Parse(PrimitiveDefinition((_, _, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement("unused"))).Metadata.GetRawText())
        }.ToJsonString();
        await provider.SendRawAsync($"Content-Length: {Encoding.UTF8.GetByteCount(response)}\r\n\r\n{response}", cancellationToken);
        var rejection = JsonNode.Parse((await provider.ReadAsync(cancellationToken))!)!.AsObject();
        Assert.Equal("PROVIDER_REGISTRATION_REJECTED", rejection["error"]!["data"]!["code"]!.GetValue<string>());
        var capabilities = await app.GetIntegrationCapabilitiesAsync(cancellationToken);
        Assert.Equal(0, capabilities.GetProperty("capabilities").GetArrayLength());
    }

    [Fact]
    public async Task AbandonedProviderAuthorityIsOwnerScopedOneUseAndWakesPendingWaits()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var foreign = await ConnectAsync(host);
        var authority = await app.ExpectIntegrationHostAsync("example.pending", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            foreign.AbandonIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
        var waiting = app.WaitIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken);
        Assert.True(await app.AbandonIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => waiting);
        Assert.False(await app.AbandonIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            foreign.RegisterIntegrationHostAsync(authority, PrimitiveDefinition((_, _, _) =>
                Task.FromResult(JsonSerializer.SerializeToElement("unused"))), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProviderHostUsesInjectedConfigurationWithoutAmbientEnvironment()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        var authority = await app.ExpectIntegrationHostAsync("example.pending", CancellationToken.None);
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_NATIVE_RPC_ENDPOINT"] = host.Endpoint.ToString(),
            ["ASPIRE_NATIVE_RPC_TOKEN"] = host.Token,
            ["ASPIRE_INTEGRATION_HOST_REGISTRATION_ID"] = authority
        });
        var provider = new NativeProviderHost(configuration, TimeProvider.System, NullLogger.Instance);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var lifetime = provider.RunAsync(PrimitiveDefinition((_, _, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement("provider-host-ok"))), shutdown.Token);
        Assert.True(await app.WaitIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
        Assert.Equal("provider-host-ok", (await app.InvokeAsync("example.pending/pending", [],
            TestContext.Current.CancellationToken)).GetString());
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime);
    }

    [Theory]
    [InlineData("""{"code":"bad","message":"private-provider-secret"}""")]
    [InlineData("""{"code":-32603,"message":false}""")]
    public async Task MalformedProviderFailureClosesOnlyProviderAndFailsActivation(string error)
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), TimeProvider.System);
        using var provider = new NativeSocketTestClient(host.Endpoint);
        await provider.CallAsync("authenticate", new JsonArray(host.Token), deadline.Token);
        var authority = await app.ExpectIntegrationHostAsync("example.malformed", deadline.Token);
        await provider.SendAsync("registerAsIntegrationHost", new JsonArray(authority), deadline.Token);
        var request = JsonNode.Parse((await provider.ReadAsync(deadline.Token))!)!.AsObject();
        Assert.Equal("getCapabilities", request["method"]!.GetValue<string>());
        // A provider reply is {"jsonrpc":"2.0","id":"native:<n>","error":...};
        // malformed code/message kinds must not expose the provider's payload.
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["error"] = JsonNode.Parse(error)
        }.ToJsonString();
        await provider.SendRawAsync($"Content-Length: {Encoding.UTF8.GetByteCount(response)}\r\n\r\n{response}", deadline.Token);
        var failure = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            app.WaitIntegrationHostRegistrationAsync(authority, deadline.Token));
        Assert.Equal("The provider activation failed.", failure.Message);
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task OfflineGeneratedProviderMetadataMatchesAuthoredCSharpApi() =>
        await Verifier.Verify(ProviderContract.Create().Metadata.GetRawText(), "json").UseDirectory("Snapshots");

    [Fact]
    public async Task IntegrationOwnedCSharpApisUseDelegatedCoreAuthorityAndNativeSecretReferences()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var authority = await app.ExpectIntegrationHostAsync("example.csharp.external-api", CancellationToken.None);
        await provider.RegisterIntegrationHostAsync(authority, ProviderContract.Create(), CancellationToken.None);
        Assert.True(await app.WaitIntegrationHostRegistrationAsync(authority, CancellationToken.None));
        var discovery = await app.GetIntegrationCapabilitiesAsync(CancellationToken.None);
        Assert.Equal(2, discovery.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(3, discovery.GetProperty("capabilities").GetArrayLength());
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var secret = await composition.AddSecretParameterAsync("api-key", "provider-must-not-read-this");
        var catalog = await composition.AddExternalApiAsync("catalog", "https://catalog.example/");
        var configured = await catalog.WithApiKeyAsync(secret);
        Assert.True(JsonElement.DeepEquals(catalog.Handle, configured.Handle));
        Assert.Equal("catalog", await catalog.GetResourceNameAsync());
        var declaration = Assert.Single((await composition.InspectAsync()).Resources);
        Assert.Equal("example/external-api", declaration.TypeId);
        Assert.Equal("https://catalog.example/", Assert.Single(declaration.Configuration).Value);
        Assert.Empty(declaration.Dependencies);
        await using var foreign = await ConnectAsync(host);
        var unauthorized = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            foreign.InvokeAsync("example.csharp.external-api/getResourceName",
                new Dictionary<string, JsonElement> { ["context"] = catalog.Handle }, CancellationToken.None));
        Assert.Equal("HANDLE_NOT_FOUND", unauthorized.Code);
        await session.RetireGenerationAsync(composition);
        var retired = await Assert.ThrowsAsync<NativeCapabilityException>(() => catalog.GetResourceNameAsync());
        Assert.Equal("HANDLE_NOT_FOUND", retired.Code);
    }

    [Fact]
    public async Task OneUseActivationRejectsReplayAndProviderDisconnectDoesNotRetireCallerModel()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        var provider = await ConnectAsync(host);
        var authority = await app.ExpectIntegrationHostAsync("example.csharp.external-api", CancellationToken.None);
        await provider.RegisterIntegrationHostAsync(authority, ProviderContract.Create(), CancellationToken.None);
        await using var replay = await ConnectAsync(host);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
            replay.RegisterIntegrationHostAsync(authority, ProviderContract.Create(), CancellationToken.None));
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var catalog = await composition.AddExternalApiAsync("catalog", "https://catalog.example/");
        await provider.DisposeAsync();
        // A failed provider call proves server-side disconnect was processed; the
        // native declaration continues to belong to the original application.
        await Assert.ThrowsAsync<NativeCapabilityException>(() => catalog.GetResourceNameAsync());
        Assert.Equal("catalog", Assert.Single((await composition.InspectAsync()).Resources).Name);
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task IntegrationResourceCannotUseCompositionInsteadOfResourceDeclarationLifetime()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var generated = ProviderContract.Create();
        var invalid = generated with
        {
            Dispatch = async (context, capability, arguments) =>
            {
                var result = await generated.Dispatch(context, capability, arguments);
                var handle = JsonNode.Parse(result.GetRawText())!.AsObject();
                handle["$lifetime"] = JsonNode.Parse(arguments.GetProperty("composition").GetRawText());
                return JsonSerializer.SerializeToElement(handle);
            }
        };
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.csharp.external-api", CancellationToken.None),
            invalid, CancellationToken.None);
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            composition.AddExternalApiAsync("catalog", "https://catalog.example/"));
        Assert.Equal("INVALID_HANDLE", failure.Code);
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task DuplicateProviderCapabilitiesAreRejectedWithoutReplacingTheOriginalProvider()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var first = await ConnectAsync(host);
        await first.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.csharp.external-api", CancellationToken.None),
            ProviderContract.Create(), CancellationToken.None);
        await using var duplicate = await ConnectAsync(host);
        var duplicateAuthority = await app.ExpectIntegrationHostAsync("example.csharp.external-api", CancellationToken.None);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => duplicate.RegisterIntegrationHostAsync(
            duplicateAuthority,
            ProviderContract.Create(), CancellationToken.None));
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        Assert.Equal("catalog", await (await composition.AddExternalApiAsync("catalog", "https://catalog.example/")).GetResourceNameAsync());
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task ProjectedCallbacksRelayToOriginalGuestWithoutChangingConnectionAuthority()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var definition = new NativeProviderDefinition(JsonDocument.Parse("""
            { "protocolVersion": 2, "capabilities": [{
              "id":"example.callback/transform","method":"transform",
              "parameters":[{"name":"callback","type":{"typeId":"callback","category":"Callback"},
                "isCallback":true,"callbackParameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
                "callbackReturnType":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"string","category":"Primitive"}
            }] }
            """).RootElement.Clone(), async (context, _, args) =>
        {
            return await context.InvokeGuestCallbackAsync(args.GetProperty("callback").GetString()!,
                new Dictionary<string, JsonElement> { ["p0"] = JsonDocument.Parse("\"hello\"").RootElement.Clone() });
        });
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.callback", CancellationToken.None), definition, CancellationToken.None);
        app.RegisterCallback("guest-transform", args => Task.FromResult(JsonDocument.Parse("\"" +
            args.GetProperty("p0").GetString() + " guest\"").RootElement.Clone()));
        var result = await app.InvokeAsync("example.callback/transform",
            new Dictionary<string, JsonElement> { ["callback"] = JsonDocument.Parse("\"guest-transform\"").RootElement.Clone() }, CancellationToken.None);
        Assert.Equal("hello guest", result.GetString());
    }

    [Fact]
    public async Task ImmediateAndFailedProviderInvocationsReleaseCallbackAuthority()
    {
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new() { MaximumHandlesPerConnection = 4 });
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        NativeProviderContext? callbackContext = null;
        string? callbackId = null;
        var definition = new NativeProviderDefinition(JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{
              "id":"example.callback/transform","method":"transform",
              "parameters":[{"name":"callback","type":{"typeId":"callback","category":"Callback"},
                "isCallback":true,"callbackParameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
                "callbackReturnType":{"typeId":"string","category":"Primitive"}},
                {"name":"value","type":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"string","category":"Primitive"}
            }]}
            """).RootElement.Clone(), async (context, _, args) =>
        {
            callbackContext = context;
            callbackId = args.GetProperty("callback").GetString()!;
            if (args.GetProperty("value").GetString() == "fail")
            {
                throw new InvalidOperationException("Provider invocation failed.");
            }

            return await context.InvokeGuestCallbackAsync(callbackId,
                new() { ["p0"] = args.GetProperty("value") });
        });
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.callback", CancellationToken.None), definition, CancellationToken.None);
        app.RegisterCallback("transform", args => Task.FromResult(args.GetProperty("p0")));
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["callback"] = JsonSerializer.SerializeToElement("transform"),
                ["value"] = JsonSerializer.SerializeToElement(42)
            };
            var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                app.InvokeAsync("example.callback/transform", arguments, CancellationToken.None));
            Assert.Equal("INVALID_ARGUMENT", failure.Code);
            arguments["value"] = JsonSerializer.SerializeToElement("hello");
            Assert.Equal("hello", (await app.InvokeAsync("example.callback/transform", arguments, CancellationToken.None)).GetString());
            Assert.NotNull(callbackContext);
            Assert.NotNull(callbackId);
            arguments["value"] = JsonSerializer.SerializeToElement("fail");
            await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                app.InvokeAsync("example.callback/transform", arguments, CancellationToken.None));
            await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
                callbackContext.InvokeGuestCallbackAsync(callbackId, new() { ["p0"] = arguments["value"] }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredProviderCallbacksFollowResourceRetirement(bool attachToExistingReceiver)
    {
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new() { MaximumHandlesPerConnection = 16 });
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        NativeProviderContext? callbackContext = null;
        string? callbackId = null;
        var definition = new NativeProviderDefinition(JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{
              "id":"example.callback/add","method":"add",
              "parameters":[{"name":"composition","type":{"typeId":"Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.Composition","category":"Handle"}},
                {"name":"callback","type":{"typeId":"callback","category":"Callback"},"isCallback":true,
                  "callbackParameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
                  "callbackReturnType":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"example.callback/Resource","category":"Handle"}
            },{
              "id":"example.callback/attach","method":"attach",
              "targetTypeId":"example.callback/Resource","targetParameterName":"resource",
              "parameters":[{"name":"resource","type":{"typeId":"example.callback/Resource","category":"Handle"}},
                {"name":"callback","type":{"typeId":"callback","category":"Callback"},"isCallback":true,
                  "callbackParameters":[{"name":"value","type":{"typeId":"string","category":"Primitive"}}],
                  "callbackReturnType":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"string","category":"Primitive"}
            }]}
            """).RootElement.Clone(), async (context, _, args) =>
        {
            callbackContext = context;
            callbackId = args.GetProperty("callback").GetString()!;
            if (args.TryGetProperty("resource", out var receiver))
            {
                return JsonSerializer.SerializeToElement("attached");
            }
            var composition = new Composition(context.Client, args.GetProperty("composition"));
            var declaration = await composition.AddResourceAsync("callback-resource", "example/callback");

            return context.PublishResource(new LifecycleProviderTestResource(declaration), "example.callback/Resource");
        });
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.callback", CancellationToken.None), definition, CancellationToken.None);
        app.RegisterCallback("transform", args => Task.FromResult(args.GetProperty("p0")));
        var session = await app.Server.OpenApplicationAsync();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var composition = await session.StartGenerationAsync();
            var resource = await app.InvokeAsync("example.callback/add", new()
            {
                ["composition"] = composition.Handle,
                ["callback"] = JsonSerializer.SerializeToElement("transform")
            }, CancellationToken.None);
            if (attachToExistingReceiver)
            {
                Assert.Equal("attached", (await app.InvokeAsync("example.callback/attach", new()
                {
                    ["resource"] = resource,
                    ["callback"] = JsonSerializer.SerializeToElement("transform")
                }, CancellationToken.None)).GetString());
            }
            Assert.NotNull(callbackContext);
            Assert.NotNull(callbackId);
            var arguments = new Dictionary<string, JsonElement> { ["p0"] = JsonSerializer.SerializeToElement("deferred") };
            Assert.Equal("deferred", (await callbackContext.InvokeGuestCallbackAsync(callbackId, arguments)).GetString());
            await session.RetireGenerationAsync(composition);
            await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() =>
                callbackContext.InvokeGuestCallbackAsync(callbackId, arguments));
        }
    }

    [Fact]
    public async Task InvocationDeadlineUsesInjectedClockAndNeverReplaysSideEffects()
    {
        var clock = new AdjustableFakeTimeProvider();
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new()
        {
            TimeProvider = clock,
            ProviderInvocationTimeout = TimeSpan.FromSeconds(5)
        });
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var definition = PrimitiveDefinition(async (_, _, _) =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            return await release.Task;
        });
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.pending", CancellationToken.None), definition, CancellationToken.None);
        var timerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.TimerCreated = due =>
        {
            if (due == TimeSpan.FromSeconds(5))
            {
                timerCreated.TrySetResult();
            }
        };
        var pending = app.InvokeAsync("example.pending/pending", [], CancellationToken.None);
        await Task.WhenAll(entered.Task, timerCreated.Task).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<NativeCapabilityException>(() => pending);
        Assert.Equal("PROVIDER_TIMEOUT", error.Code);
        Assert.Equal(1, Volatile.Read(ref count));
        release.TrySetResult(JsonDocument.Parse("\"late\"").RootElement.Clone());
        var unavailable = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            app.InvokeAsync("example.pending/pending", [], CancellationToken.None));
        Assert.Equal("UNKNOWN_CAPABILITY", unavailable.Code);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ProviderDisconnectFailsPendingInvocationWithoutWaitingForItsDeadline()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        var provider = await ConnectAsync(host);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.pending", CancellationToken.None),
            PrimitiveDefinition(async (_, _, _) => { entered.TrySetResult(); return await release.Task; }), CancellationToken.None);
        var pending = app.InvokeAsync("example.pending/pending", [], CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
        await provider.DisposeAsync();
        var error = await Assert.ThrowsAsync<NativeCapabilityException>(() => pending);
        Assert.Equal("PROVIDER_DISCONNECTED", error.Code);
        release.TrySetResult(JsonDocument.Parse("\"late\"").RootElement.Clone());
    }

    [Fact]
    public async Task ProvidersCannotReplaceCoreCapabilities()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var metadata = JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"Aspire.Hosting.Native.Api/addResource","method":"addResource",
              "parameters":[],"returnType":{"typeId":"void","category":"Primitive"}}]}
            """).RootElement.Clone();
        var authority = await app.ExpectIntegrationHostAsync("Aspire.Hosting.Native.Api", CancellationToken.None);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => provider.RegisterIntegrationHostAsync(
            authority,
            new(metadata, (_, _, _) => Task.FromResult(default(JsonElement))), CancellationToken.None));
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        Assert.Equal("core-still-works", (await (await composition.AddResourceAsync("core-still-works", "example/core")).InspectResourceAsync()).Name);
        await session.RetireGenerationAsync(composition);
    }

    private static NativeProviderDefinition PrimitiveDefinition(Func<NativeProviderContext, string, JsonElement, Task<JsonElement>> dispatch) =>
        new(JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.pending/pending","method":"pending","parameters":[],
             "returnType":{"typeId":"string","category":"Primitive"}}]}
            """).RootElement.Clone(), dispatch);

    private static Task<NativeClient> ConnectAsync(NativeClientTestHost host) =>
        NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);

    [Fact]
    public async Task CallbackHandleArgumentsAreTranslatedBackToTheirOriginalGuestIdentity()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var definition = new NativeProviderDefinition(JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.handlecallback/inspect","method":"inspect",
              "parameters":[
                {"name":"declaration","type":{"typeId":"Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.DeclaredResource","category":"Handle"}},
                {"name":"callback","type":{"typeId":"callback","category":"Callback"},"isCallback":true,
                  "callbackParameters":[{"name":"resource","type":{"typeId":"Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.DeclaredResource","category":"Handle"}}],
                  "callbackReturnType":{"typeId":"string","category":"Primitive"}}],
              "returnType":{"typeId":"string","category":"Primitive"}}]}
            """).RootElement.Clone(), async (context, _, arguments) => await context.InvokeGuestCallbackAsync(
            arguments.GetProperty("callback").GetString()!, new Dictionary<string, JsonElement> { ["p0"] = arguments.GetProperty("declaration") }));
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.handlecallback", CancellationToken.None), definition, CancellationToken.None);
        var session = await app.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        var resource = await composition.AddResourceAsync("original", "example/resource");
        app.RegisterCallback("inspect-original", async arguments =>
        {
            Assert.Equal(resource.Handle.GetRawText(), arguments.GetProperty("p0").GetRawText());
            var original = new DeclaredResource(app, arguments.GetProperty("p0"));
            return JsonDocument.Parse("\"" + (await original.InspectResourceAsync()).Name + "\"").RootElement.Clone();
        });
        var result = await app.InvokeAsync("example.handlecallback/inspect",
            new Dictionary<string, JsonElement>
            {
                ["declaration"] = resource.Handle,
                ["callback"] = JsonDocument.Parse("\"inspect-original\"").RootElement.Clone()
            }, CancellationToken.None);
        Assert.Equal("original", result.GetString());
        await session.RetireGenerationAsync(composition);
    }

    [Theory]
    [InlineData("""{"protocolVersion":1,"capabilities":[]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example.invalid/unsupported","method":"unsupported","parameters":[],"returnType":{"typeId":"example.invalid/Dto","category":"Dto"}}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"example.invalid/receiver","method":"receiver","parameters":[],"returnType":{"typeId":"string","category":"Primitive"},"targetTypeId":"example.invalid/Resource","targetParameterName":"context"}]}""")]
    [InlineData("""{"protocolVersion":2,"capabilities":[{"id":"outside/namespace","method":"invalid","parameters":[],"returnType":{"typeId":"string","category":"Primitive"}}]}""")]
    public async Task InvalidMetadataRejectsActivationAndUnblocksTheWaitingApplication(string metadata)
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var authority = await app.ExpectIntegrationHostAsync("example.invalid", CancellationToken.None);
        var waiting = app.WaitIntegrationHostRegistrationAsync(authority, CancellationToken.None);
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => provider.RegisterIntegrationHostAsync(
            authority, new(JsonDocument.Parse(metadata).RootElement.Clone(), (_, _, _) => Task.FromResult(default(JsonElement))), CancellationToken.None));
        await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => waiting);
    }
}
