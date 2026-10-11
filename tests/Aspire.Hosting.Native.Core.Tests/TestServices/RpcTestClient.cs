// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Api;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Exercises the actual JSON-RPC envelope without a transport or service-provider fake.</summary>
internal sealed class RpcTestClient : IDisposable
{
    public const string Token = "native-rpc-test-token-not-a-real-secret";
    private int _requestId;

    public RpcTestClient(bool authenticate) : this(new NativeRpcConnection(Token), authenticate)
    {
    }

    public RpcTestClient(NativeApplicationServer server) : this(new NativeRpcConnection(Token, server), authenticate: true)
    {
    }

    public RpcTestClient(NativeApplicationServer server, NativeLanguageCatalog languages)
        : this(new NativeRpcConnection(Token, server, languages), authenticate: true)
    {
    }

    private RpcTestClient(NativeRpcConnection connection, bool authenticate)
    {
        Connection = connection;
        if (authenticate && Call("authenticate", new JsonArray(Token))["result"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("The test RPC connection could not authenticate.");
        }
    }

    public NativeRpcConnection Connection { get; }

    public JsonObject Call(string method, JsonNode? parameters) => CallAsync(method, parameters).GetAwaiter().GetResult();

    public async Task<JsonObject> CallAsync(string method, JsonNode? parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _requestId),
            ["method"] = method,
            ["params"] = parameters
        };
        var response = await Connection.ProcessAsync(Encoding.UTF8.GetBytes(request.ToJsonString())).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A request did not receive a response.");

        return JsonNode.Parse(response)?.AsObject()
            ?? throw new InvalidOperationException("The RPC response is not a JSON object.");
    }

    public JsonObject Invoke(string capability, JsonObject arguments) =>
        Call("invokeCapability", new JsonArray(CapabilityId(capability), arguments));

    public Task<JsonObject> InvokeAsync(string capability, JsonObject arguments) =>
        CallAsync("invokeCapability", new JsonArray(CapabilityId(capability), arguments));

    // ATS static exports use the assembly name; instance exports use the type namespace.
    private static string CapabilityId(string capability) =>
        (capability == "createSession" ? "Aspire.Hosting.Native.Server/" : "Aspire.Hosting.Native.Api/") + capability;

    public JsonNode CreateSession() => Result(Invoke("createSession", []));

    public JsonNode StartGeneration(JsonNode session) =>
        Result(Invoke("startGeneration", Arguments(("context", session))));

    public JsonNode AddResource(JsonNode composition, string name) =>
        Result(Invoke("addResource", Arguments(("context", composition), ("name", JsonValue.Create(name)),
            ("typeId", JsonValue.Create("example/Resource")))));

    public static JsonObject Arguments(params (string Name, JsonNode? Value)[] values) =>
        new(values.Select(value => KeyValuePair.Create(value.Name, value.Value?.DeepClone())));

    public static JsonNode Result(JsonObject response) => response["result"]?.DeepClone()
        ?? throw new InvalidOperationException("The RPC request did not return a non-null result.");

    public void Dispose() => Connection.Dispose();
}
