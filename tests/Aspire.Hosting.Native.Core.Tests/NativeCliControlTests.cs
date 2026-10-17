// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Rpc;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCliControlTests
{
    [Fact]
    public void CliReceivesOfflineSdkAndLanguageSpecWithoutChangingAtsDiscovery()
    {
        var server = new NativeApplicationServer();
        using var client = new RpcTestClient(server, NativeLanguageCatalog.LoadEmbedded());
        var spec = Result(client.Call("getRuntimeSpec", new JsonArray("typescript/nodejs")));
        Assert.Equal("typescript/nodejs", spec["language"]!.GetValue<string>());
        Assert.Equal("npx", spec["execute"]!["command"]!.GetValue<string>());
        var sdk = Result(client.Call("generateCode", new JsonArray("TypeScript", null))).AsObject();
        Assert.Equal(["apphost-server.json", "aspire.mts", "base.mts", "integration-host.mts", "native-builder.mts", "native-client.mts", "native-pipeline.mts", "native-types.mts", "transport.mts"],
            sdk.Select(file => file.Key).Order(StringComparer.Ordinal));
        Assert.All(sdk, file => Assert.False(string.IsNullOrWhiteSpace(file.Value!.GetValue<string>())));
        Assert.Contains("from './native-pipeline.mjs'", sdk["native-builder.mts"]!.GetValue<string>());
        var identity = JsonNode.Parse(sdk["apphost-server.json"]!.GetValue<string>())!;
        Assert.Equal(2, identity["generation"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(identity["contractId"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(identity["version"]!.GetValue<string>()));
        Assert.NotEmpty(Result(client.Call("getCapabilities", null))["Capabilities"]!.AsArray());
        Assert.Empty(Result(client.Call("getAppHostServerConfiguration", null)).AsArray());
        Assert.Equal("INVALID_ARGUMENT", Error(client.Call("getAppHostServerConfiguration", new JsonArray("unexpected"))));
        Assert.Equal("INVALID_ARGUMENT", Error(client.Call("generateCode", new JsonArray("TypeScript", "Aspire.Hosting"))));
        Assert.Equal("NOT_SUPPORTED", Error(client.Call("getRuntimeSpec", new JsonArray("python"))));
    }

    [Theory]
    [InlineData("getRuntimeState")]
    [InlineData("requestStop")]
    [InlineData("notifyCliReady")]
    public void NativeSpecificControlMethodsAreNotPartOfTheBootstrapContract(string method)
    {
        var server = new NativeApplicationServer();
        using var client = new RpcTestClient(server, NativeLanguageCatalog.LoadEmbedded());
        Assert.Equal("METHOD_NOT_FOUND", Error(client.Call(method, null)));
    }

    [Fact]
    public void StreamJsonRpcTraceContextDoesNotInvalidateAuthenticationEnvelope()
    {
        using var capture = new DiagnosticCapture(sampleActivities: false);
        using var connection = new NativeRpcConnection(Token);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "authenticate", ["params"] = new JsonArray(Token),
            ["traceparent"] = capture.Root.Id,
            ["tracestate"] = "vendor=value"
        };
        var response = connection.Process(Encoding.UTF8.GetBytes(request.ToJsonString()));
        Assert.NotNull(response);
        Assert.True(JsonNode.Parse(response)!["result"]!.GetValue<bool>());
        Assert.All(capture.Events, entry => Assert.Equal(capture.Root.TraceId.ToString(), entry.TraceId));
        Assert.Equal(2, capture.Events.Count);
    }

    private static string Error(JsonObject response) => response["error"]!["data"]!["code"]!.GetValue<string>();
}
