// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Rpc;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCliControlTests
{
    [Fact]
    public async Task CliReceivesOfflineSdkAndLanguageSpecWithoutChangingAtsDiscovery()
    {
        var server = new NativeApplicationServer();
        using var client = new RpcTestClient(server, NativeLanguageCatalog.LoadEmbedded());
        var spec = Result(client.Call("getRuntimeSpec", new JsonArray("typescript/nodejs")));
        var sdk = Result(client.Call("generateCode", new JsonArray("TypeScript", null))).AsObject();
        Assert.All(sdk, file => Assert.False(string.IsNullOrWhiteSpace(file.Value!.GetValue<string>())));
        var assembly = typeof(NativeLanguageCatalog).Assembly;
        foreach (var file in sdk.Where(file => file.Key != "apphost-server.json"))
        {
            using var stream = assembly.GetManifestResourceStream("NativeSdk." + file.Key);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            Assert.Equal(await reader.ReadToEndAsync(TestContext.Current.CancellationToken), file.Value!.GetValue<string>());
        }
        var identity = JsonNode.Parse(sdk["apphost-server.json"]!.GetValue<string>())!;
        using var contract = assembly.GetManifestResourceStream("Aspire.Hosting.Native.Rpc.contract.json");
        Assert.NotNull(contract);
        var contractHash = new XxHash3();
        await contractHash.AppendAsync(contract, TestContext.Current.CancellationToken);
        Assert.Equal(Convert.ToHexString(contractHash.GetCurrentHash()), identity["contractId"]!.GetValue<string>());
        Assert.Equal(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            identity["version"]!.GetValue<string>());
        // The fingerprint identifies this build's embedded bytes, not a platform-independent API shape.
        identity["contractId"] = "contract-id";
        identity["version"] = "server-version";
        // Normalize embedded file line endings before JSON escapes them for the snapshot.
        foreach (var (name, content) in spec["migrationFiles"]!.AsObject().ToArray())
        {
            spec["migrationFiles"]![name] = content!.GetValue<string>().ReplaceLineEndings("\n");
        }
        await Verify(JsonSerializer.Serialize(new
        {
            runtimeSpec = spec,
            sdkFiles = sdk.Select(file => file.Key).Order(StringComparer.Ordinal),
            identity
        }, new JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
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
