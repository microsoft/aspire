// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Rpc;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeRpcTests
{
    [Fact]
    public async Task GeneratedContractMatchesImplementedCapabilities()
    {
        using var client = new RpcTestClient(authenticate: true);
        var contract = Result(client.Call("getCapabilities", null));
        await Verifier.Verify(contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
    }

    [Fact]
    public void AuthenticationPrecedesDiscoveryAndInvocation()
    {
        using var client = new RpcTestClient(authenticate: false);
        Assert.Equal("UNAUTHENTICATED", Error(client.Call("getCapabilities", null)));
        Assert.Equal("UNAUTHENTICATED", Error(client.Invoke("createSession", [])));
        Assert.False(client.Call("authenticate", new JsonArray("wrong-token"))["result"]!.GetValue<bool>());
        Assert.Equal(0, client.Connection.HandleCount);
        Assert.True(client.Call("authenticate", new JsonArray(Token))["result"]!.GetValue<bool>());
        Assert.NotNull(client.CreateSession());
    }

    [Fact]
    public void GeneratedCapabilitiesComposeAndSealResources()
    {
        using var client = new RpcTestClient(authenticate: true);
        var session = client.CreateSession();
        var composition = client.StartGeneration(session);
        var web = client.AddResource(composition, "web");
        var cache = client.AddResource(composition, "cache");
        var wait = client.Invoke("waitFor", Arguments(("context", web), ("dependency", cache)));
        Assert.Null(wait["error"]);
        Assert.Null(wait["result"]);
        var snapshot = Result(client.Invoke("seal", Arguments(("context", composition))));
        Assert.Collection(snapshot["resources"]!.AsArray(),
            resource =>
            {
                Assert.Equal("cache", resource!["name"]!.GetValue<string>());
                Assert.Empty(resource["dependencies"]!.AsArray());
            },
            resource =>
            {
                Assert.Equal("web", resource!["name"]!.GetValue<string>());
                Assert.Equal(snapshot["resources"]![0]!["resourceId"]!.GetValue<string>(),
                    Assert.Single(resource["dependencies"]!.AsArray())!.GetValue<string>());
            });
        Assert.Equal(snapshot.ToJsonString(), Result(client.Invoke("inspect", Arguments(("context", composition)))).ToJsonString());
        Assert.Equal("OPERATION_REJECTED", Error(client.Invoke("addResource",
            Arguments(("context", composition), ("name", JsonValue.Create("late")), ("typeId", JsonValue.Create("example/Resource"))))));
    }

    [Fact]
    public void RetirementRevokesAndReleasesGenerationHandles()
    {
        using var client = new RpcTestClient(authenticate: true);
        var session = client.CreateSession();
        for (var index = 0; index < 100; index++)
        {
            var composition = client.StartGeneration(session);
            var resource = client.AddResource(composition, "cache");
            Assert.Equal(3, client.Connection.HandleCount);
            Assert.Null(client.Invoke("retireGeneration", Arguments(("context", session), ("composition", composition)))["error"]);
            Assert.Equal(1, client.Connection.HandleCount);
            Assert.Equal("HANDLE_NOT_FOUND", Error(client.Invoke("inspectResource", Arguments(("context", resource)))));
        }
        var replacement = client.StartGeneration(session);
        Assert.Empty(Result(client.Invoke("inspect", Arguments(("context", replacement))))["resources"]!.AsArray());
    }

    [Fact]
    public void HandleSweepRemovesInterleavedRetiredEntriesWithoutLosingLiveAuthority()
    {
        using var client = new RpcTestClient(authenticate: true);
        var retiredSession = client.CreateSession();
        var liveSession = client.CreateSession();
        var retired = client.StartGeneration(retiredSession);
        var live = client.StartGeneration(liveSession);
        var liveResources = new JsonNode[128];
        for (var index = 0; index < liveResources.Length; index++)
        {
            client.AddResource(retired, $"retired-{index}");
            liveResources[index] = client.AddResource(live, $"live-{index}");
        }
        Assert.Equal(260, client.Connection.HandleCount);
        Assert.Null(client.Invoke("retireGeneration", Arguments(("context", retiredSession), ("composition", retired)))["error"]);
        Assert.Equal(131, client.Connection.HandleCount);
        for (var index = 0; index < liveResources.Length; index++)
        {
            Assert.Equal($"live-{index}", Result(client.Invoke("inspectResource",
                Arguments(("context", liveResources[index]))))["name"]!.GetValue<string>());
        }
        var replacement = client.StartGeneration(retiredSession);
        var added = client.AddResource(replacement, "replacement");
        Assert.Equal("replacement", Result(client.Invoke("inspectResource", Arguments(("context", added))))["name"]!.GetValue<string>());
        Assert.Equal(133, client.Connection.HandleCount);
    }

    [Fact]
    public void ForeignForgedAndRetaggedHandlesAreRejected()
    {
        using var first = new RpcTestClient(authenticate: true);
        using var second = new RpcTestClient(authenticate: true);
        var composition = first.StartGeneration(first.CreateSession());
        var resource = first.AddResource(composition, "cache");
        Assert.Equal("HANDLE_NOT_FOUND", Error(second.Invoke("inspectResource", Arguments(("context", resource)))));
        var forged = resource.DeepClone();
        forged["$handle"] = "not-issued";
        Assert.Equal("HANDLE_NOT_FOUND", Error(first.Invoke("inspectResource", Arguments(("context", forged)))));
        var retagged = resource.DeepClone();
        retagged["$type"] = composition["$type"]!.DeepClone();
        Assert.Equal("TYPE_MISMATCH", Error(first.Invoke("inspectResource", Arguments(("context", retagged)))));
        Assert.Equal("cache", Result(first.Invoke("inspectResource", Arguments(("context", resource))))["name"]!.GetValue<string>());
    }

    [Fact]
    public void SymbolicValueParametersRejectForeignAuthorityAndRetirementReleasesTheirHandles()
    {
        using var first = new RpcTestClient(authenticate: true);
        using var second = new RpcTestClient(authenticate: true);
        var session = first.CreateSession();
        var composition = first.StartGeneration(session);
        var value = Result(first.Invoke("addSecretParameter", Arguments(("context", composition),
            ("name", JsonValue.Create("password")), ("value", JsonValue.Create("rpc-secret")))));
        var resource = second.AddResource(second.StartGeneration(second.CreateSession()), "consumer");
        Assert.Equal("HANDLE_NOT_FOUND", Error(second.Invoke("setSymbolicConfiguration",
            Arguments(("context", resource), ("name", JsonValue.Create("password")), ("value", value)))));
        Assert.Equal(3, first.Connection.HandleCount);
        Assert.Null(first.Invoke("retireGeneration", Arguments(("context", session), ("composition", composition)))["error"]);
        Assert.Equal(1, first.Connection.HandleCount);
        Assert.Equal("HANDLE_NOT_FOUND", Error(first.Invoke("encodeUriValue", Arguments(("context", value)))));
    }

    [Fact]
    public void WrongSessionCannotRetireAnotherComposition()
    {
        using var client = new RpcTestClient(authenticate: true);
        var first = client.CreateSession();
        var second = client.CreateSession();
        var composition = client.StartGeneration(first);
        Assert.Equal("INVALID_ARGUMENT", Error(client.Invoke("retireGeneration", Arguments(("context", second), ("composition", composition)))));
        Assert.Empty(Result(client.Invoke("inspect", Arguments(("context", composition))))["resources"]!.AsArray());
    }

    [Fact]
    public void FailedCallsPreserveCompositionAndRedactInput()
    {
        using var client = new RpcTestClient(authenticate: true);
        var composition = client.StartGeneration(client.CreateSession());
        client.AddResource(composition, "private-secret-name");
        var duplicate = client.Invoke("addResource", Arguments(("context", composition),
            ("name", JsonValue.Create("private-secret-name")), ("typeId", JsonValue.Create("example/Resource"))));
        Assert.Equal("OPERATION_REJECTED", Error(duplicate));
        Assert.Equal("The operation is not allowed in the current state.", duplicate["result"]!["$error"]!["message"]!.GetValue<string>());
        Assert.Single(Result(client.Invoke("inspect", Arguments(("context", composition))))["resources"]!.AsArray());
        Assert.Equal("INVALID_ARGUMENT", Error(client.Invoke("createSession", new JsonObject { ["unexpected"] = true })));
        Assert.Equal("CAPABILITY_NOT_FOUND", Error(client.Invoke("doesNotExist", [])));
    }

    [Theory]
    [InlineData("{", "PARSE_ERROR")]
    [InlineData("[]", "INVALID_REQUEST")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"authenticate\"}", "INVALID_REQUEST")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"method\":\"authenticate\"}", "INVALID_REQUEST")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"invokeCapability\",\"params\":[\"x\",{\"a\":1,\"a\":2}]}", "INVALID_REQUEST")]
    public void InvalidRequestsProduceClassifiedErrors(string payload, string classification)
    {
        using var connection = new NativeRpcConnection(Token);
        var response = connection.Process(Encoding.UTF8.GetBytes(payload));
        Assert.Equal(classification, Error(JsonNode.Parse(response!)!.AsObject()));
    }

    [Fact]
    public void NotificationsHaveNoResponseAndDisposalRejectsFurtherCalls()
    {
        var connection = new NativeRpcConnection(Token);
        Assert.Null(connection.Process(Encoding.UTF8.GetBytes(
            $"{{\"jsonrpc\":\"2.0\",\"method\":\"authenticate\",\"params\":[\"{Token}\"]}}")));
        Assert.Null(connection.Process(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"invokeCapability\",\"params\":[\"unknown\",{}]}")));
        connection.Dispose();
        connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.Process("{}"u8.ToArray()));
    }

    [Fact]
    public void RequestSizeAndHandleCapacityAreExplicitAndAtomic()
    {
        using var connection = new NativeRpcConnection(Token);
        Assert.Equal("REQUEST_TOO_LARGE", Error(JsonNode.Parse(connection.Process(new byte[Aspire.Hosting.Native.Server.NativeServerOptions.DefaultMaximumRequestBytes + 1])!)!.AsObject()));
        using var client = new RpcTestClient(authenticate: true);
        var composition = client.StartGeneration(client.CreateSession());
        for (var index = 0; index < 4094; index++)
        {
            client.AddResource(composition, $"r{index}");
        }
        var rejected = client.Invoke("addResource", Arguments(("context", composition),
            ("name", JsonValue.Create("overflow")), ("typeId", JsonValue.Create("example/Resource"))));
        Assert.Equal("HANDLE_LIMIT", Error(rejected));
        Assert.Equal(4094, Result(client.Invoke("inspect", Arguments(("context", composition))))["resources"]!.AsArray().Count);
    }

    [Fact]
    public async Task ConcurrentRequestsRetainDistinctResources()
    {
        using var client = new RpcTestClient(authenticate: true);
        var composition = client.StartGeneration(client.CreateSession());
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() => client.AddResource(composition, $"r{index}"))));
        Assert.Equal(32, Result(client.Invoke("inspect", Arguments(("context", composition))))["resources"]!.AsArray().Count);
    }

    private static string Error(JsonObject response) =>
        (response["result"]?["$error"]?["code"] ?? response["error"]!["data"]!["code"])!.GetValue<string>();
}
