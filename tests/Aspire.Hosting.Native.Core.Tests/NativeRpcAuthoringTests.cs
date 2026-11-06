// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeRpcAuthoringTests
{
    [Fact]
    public async Task GeneratedAuthoringCallsKeepDeclarationsInertAndResolveValuesOnlyAtExecution()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        await using var client = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var source = await builder.AddContainerAsync("cache", "localhost:5000/team/redis:old");
        await source.WithImageTagAsync("8-alpine");
        await source.WithEndpointAsync(Endpoint("tcp", 6379, "redis"));
        await source.WithArgsAsync(["--save", ""]);
        var password = await builder.AddSecretParameterAsync("password", "native-private-secret");
        var composition = await builder.BuildAsync();
        var connection = await composition.CreateLiteralValueAsync("redis://:");
        connection = await connection.ConcatValueAsync(password);
        connection = await connection.ConcatValueAsync(await composition.CreateLiteralValueAsync("@"));
        var endpoint = await source.GetEndpointAsync("tcp");
        connection = await connection.ConcatValueAsync(await endpoint.PropertyAsync("hostAndPort"));
        await source.WithConnectionStringAsync(connection);
        await source.WithSerializedAnnotationAsync("example.cache/v1", """{"version":1}""");
        await source.WithSerializedAnnotationAsync("example.other/v1", """{"enabled":true}""");
        await source.WithSerializedAnnotationAsync("example.cache/v1", """{"version":2}""");
        Assert.Equal("""{"version":2}""", await source.GetSerializedAnnotationAsync("example.cache/v1"));
        Assert.Equal("""{"enabled":true}""", await source.GetSerializedAnnotationAsync("example.other/v1"));
        Assert.Null(await source.GetSerializedAnnotationAsync("missing"));
        var consumer = await builder.AddExecutableAsync("consumer", Environment.ProcessPath!, AppContext.BaseDirectory);
        await consumer.WithReferenceAsync(source);
        await consumer.WithEnvironmentAsync("ADDRESS", endpoint);
        await consumer.WithEnvironmentAsync("PASSWORD", password);
        RpcEnvironmentEditor? environmentEditor = null;
        await consumer.WithEnvironmentCallbackAsync(async editor =>
        {
            environmentEditor = editor;
            await editor.SetAsync("CALLBACK", "edited");
        });
        await consumer.WithArgsCallbackAsync(async editor =>
        {
            await editor.AddAsync("--endpoint");
            await editor.AddAsync(await endpoint.PropertyAsync("hostAndPort"));
        });
        Assert.Empty(executor.Plans);
        Assert.NotNull(environmentEditor);
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            environmentEditor.SetAsync("STALE", "must-not-apply"))).Code);
        var execution = await (await builder.BuildAsync()).StartApplicationAsync();
        await execution.WaitApplicationReadyAsync(15000);
        var container = Assert.Single(executor.Plans.OfType<ContainerWorkload>());
        Assert.Equal("localhost:5000/team/redis:8-alpine", container.Image);
        Assert.Equal(["--save", ""], container.Arguments);
        var executable = Assert.Single(executor.Plans.OfType<ExecutableWorkload>());
        Assert.Equal(["--endpoint", "127.0.0.1:5050"], executable.Arguments);
        Assert.Equal(
            ["ADDRESS=redis://127.0.0.1:5050", "CALLBACK=edited",
                "ConnectionStrings__cache=redis://:native-private-secret@127.0.0.1:5050", "PASSWORD=native-private-secret"],
            executable.Environment.Select(variable => $"{variable.Name}={variable.Value}").Order(StringComparer.Ordinal));
        await builder.RetireAsync();
        Assert.Equal(2, executor.Removed.Count);
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            source.GetResourceNameAsync())).Code);
    }

    [Fact]
    public async Task ActiveRemoteEditorCallbacksBlockBuildAndAreRevokedOnFailure()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RpcEnvironmentEditor? retained = null;
        var editing = resource.WithEnvironmentCallbackAsync(async editor =>
        {
            retained = editor;
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("private-callback-secret");
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            Assert.Equal("OPERATION_REJECTED", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                builder.BuildAsync())).Code);
        }
        finally
        {
            release.TrySetResult();
        }
        var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() => editing);
        Assert.Equal("PROVIDER_FAILED", failure.Code);
        Assert.Equal("The provider rejected the invocation.", failure.Message);
        Assert.NotNull(retained);
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            retained.SetAsync("STALE", "value"))).Code);
        await builder.BuildAsync();
        await builder.RetireAsync();
    }

    [Fact]
    public async Task RetirementDuringCallbackCannotPublishARevivedFluentHandle()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editing = resource.WithArgsCallbackAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            await builder.RetireAsync();
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() => editing)).Code);
    }

    [Fact]
    public async Task RemoteEditorDeadlineRevokesAuthorityWithoutClosingTheAuthenticatedConnection()
    {
        var clock = new FakeTimeProvider();
        await using var host = await NativeClientTestHost.StartWithOptionsAsync(new()
        {
            TimeProvider = clock,
            ProviderInvocationTimeout = TimeSpan.FromSeconds(5)
        });
        await using var client = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RpcArgsEditor? retained = null;
        var editing = resource.WithArgsCallbackAsync(async editor =>
        {
            retained = editor;
            entered.TrySetResult();
            await release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                editing.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System));
            Assert.NotNull(retained);
            Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                retained.AddAsync("late"))).Code);
            await builder.BuildAsync();
        }
        finally
        {
            release.TrySetResult();
        }
        await builder.RetireAsync();
    }

    [Fact]
    public async Task SymbolicUnionInputsRejectOtherCompositionsAndForeignConnections()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        await using var foreign = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var other = await client.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        var otherValue = await other.AddSecretParameterAsync("password", "private-secret");
        Assert.Equal("INVALID_ARGUMENT", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            resource.WithEnvironmentAsync("PASSWORD", otherValue))).Code);
        var foreignResource = await (await foreign.Server.CreateRpcBuilderAsync()).AddContainerAsync("foreign", "example/service:1");
        await Assert.ThrowsAsync<ArgumentException>(() => resource.WithEnvironmentAsync("PASSWORD", foreignResource));
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["context"] = client.GetHandle(resource),
            ["name"] = JsonSerializer.SerializeToElement("PASSWORD"),
            ["value"] = foreign.GetHandle(foreignResource)
        };
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            client.InvokeAsync("Aspire.Hosting.Native.Authoring/withEnvironment", arguments, TestContext.Current.CancellationToken))).Code);
        arguments["value"] = JsonSerializer.SerializeToElement(42);
        Assert.Equal("INVALID_ARGUMENT", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            client.InvokeAsync("Aspire.Hosting.Native.Authoring/withEnvironment", arguments, TestContext.Current.CancellationToken))).Code);
        await builder.RetireAsync();
        await other.RetireAsync();
    }

    [Fact]
    public async Task VoidEditorCallbacksRejectReturnedValuesAndMutatedWritebackEnvelopes()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        var builder = await client.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        foreach (var payload in new[] { "42", """{"p0":{"$handle":"foreign","$type":"fake"}}""" })
        {
            const string callbackId = "invalid-void-callback";
            RpcEnvironmentEditor? retained = null;
            client.RegisterCallback(callbackId, arguments =>
            {
                retained = new RpcEnvironmentEditor(client, arguments.GetProperty("p0"));

                return Task.FromResult(JsonDocument.Parse(payload).RootElement.Clone());
            });
            try
            {
                var failure = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                    client.InvokeAsync("Aspire.Hosting.Native.Authoring/withEnvironmentCallback",
                        new()
                        {
                            ["context"] = client.GetHandle(resource),
                            ["callback"] = JsonSerializer.SerializeToElement(callbackId)
                        }, TestContext.Current.CancellationToken));
                Assert.Equal("INVALID_ARGUMENT", failure.Code);
                Assert.NotNull(retained);
                Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                    retained.SetAsync("STALE", "must-not-apply"))).Code);
            }
            finally
            {
                client.UnregisterCallback(callbackId);
            }
        }
        await builder.RetireAsync();
    }

    [Fact]
    public async Task ProviderAuthoringCallbacksUseProviderPeerAndBorrowOnlyGenerationScopedEditorAuthority()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var app = await ConnectAsync(host);
        await using var provider = await ConnectAsync(host);
        var metadata = JsonDocument.Parse("""
            {"protocolVersion":2,"capabilities":[{"id":"example.authoring/configure","method":"configure",
              "parameters":[{"name":"resource","type":{"typeId":"Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Authoring.RpcResource","category":"Handle"}}],
              "returnType":{"typeId":"string","category":"Primitive"}}]}
            """).RootElement.Clone();
        RpcEnvironmentEditor? retained = null;
        await provider.RegisterIntegrationHostAsync(
            await app.ExpectIntegrationHostAsync("example.authoring", TestContext.Current.CancellationToken),
            new NativeProviderDefinition(metadata, async (_, _, arguments) =>
            {
                var resource = new RpcResource(provider, arguments.GetProperty("resource"));
                await resource.WithEnvironmentCallbackAsync(async editor =>
                {
                    retained = editor;
                    await editor.SetAsync("FROM_PROVIDER", "edited");
                    await resource.WithSerializedAnnotationAsync("example.provider/v1", """{"configured":true}""");
                });

                return JsonSerializer.SerializeToElement("configured");
            }), TestContext.Current.CancellationToken);
        var builder = await app.Server.CreateRpcBuilderAsync();
        var resource = await builder.AddContainerAsync("service", "example/service:1");
        var result = await app.InvokeAsync("example.authoring/configure",
            new() { ["resource"] = app.GetHandle(resource) }, TestContext.Current.CancellationToken);
        Assert.Equal("configured", result.GetString());
        Assert.Equal("""{"configured":true}""", await resource.GetSerializedAnnotationAsync("example.provider/v1"));
        Assert.NotNull(retained);
        Assert.Equal("HANDLE_NOT_FOUND", (await Assert.ThrowsAsync<NativeCapabilityException>(() =>
            retained.SetAsync("STALE", "must-not-apply"))).Code);
        await builder.RetireAsync();
    }

    [Fact]
    public async Task GeneratedTypeScriptAuthoringUsesFluentCallsAndRealNestedCallbackRpc()
    {
        var script = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_RPC_AUTHORING_TYPESCRIPT");
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (script is null || node is null)
        {
            Assert.Skip("Supply the compiled RPC authoring AppHost and Node executable to exercise the generated TS SDK.");
        }
        await using var host = await NativeClientTestHost.StartAsync();
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Assert.IsType<IPEndPoint>(host.Endpoint).Port.ToString(CultureInfo.InvariantCulture));
        start.Environment["ASPIRE_NATIVE_RPC_TOKEN"] = host.Token;
        var output = await NativeIntegrationAppHostRunner.RunAsync(start, TimeProvider.System);
        Assert.Equal("rpc-authoring-ok", output.Trim());
    }

    private static RpcEndpointOptions Endpoint(string name, int port, string scheme) => new()
    {
        Name = name, TargetPort = port, Scheme = scheme, Protocol = null, Port = null, Env = null
    };

    private static Task<NativeClient> ConnectAsync(NativeClientTestHost host) =>
        NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System);
}
