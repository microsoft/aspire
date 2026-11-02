// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Connects independent author, integration, and observer clients to the same native server bindings.</summary>
internal sealed class RuntimeRpcTestContext : IDisposable
{
    private readonly NativeApplicationServer _server;
    public RpcTestClient Author { get; }
    public RpcTestClient Integration { get; }
    public RpcTestClient Observer { get; }
    public JsonNode Session { get; }
    public JsonNode Composition { get; }
    public JsonNode Resource { get; }
    public JsonNode Execution { get; }
    public JsonNode Writer { get; }
    public JsonNode Reader { get; }
    public NativeApplicationServer Server => _server;

    public RuntimeRpcTestContext() : this(UnavailableWorkloadExecutor.Instance)
    {
    }

    public RuntimeRpcTestContext(IWorkloadExecutor executor) : this(executor, new())
    {
    }

    public RuntimeRpcTestContext(IWorkloadExecutor executor, NativeRuntimeOptions options)
    {
        _server = new(executor, options);
        Author = new(_server);
        Integration = new(_server);
        Observer = new(_server);
        var root = Result(Author.Call("getApplicationServer", null));
        Session = Result(Author.Invoke("openApplication", Arguments(("context", root))));
        Composition = Author.StartGeneration(Session);
        Resource = Author.AddResource(Composition, "cache");
        Execution = Result(Author.Invoke("createExecution", Arguments(("context", Composition))));
        Writer = Result(Integration.Invoke("joinResourceExecution", Arguments(
            ("context", Result(Integration.Call("getApplicationServer", null))),
            ("invitation", Result(Author.Invoke("inviteResourceExecution", Arguments(("context", Execution), ("resource", Resource))))))));
        Reader = Result(Observer.Invoke("joinApplicationObserver", Arguments(
            ("context", Result(Observer.Call("getApplicationServer", null))),
            ("invitation", Result(Author.Invoke("inviteApplicationObserver", Arguments(("context", Execution))))))));
    }

    public JsonObject Publish(string state, bool healthy, params string[] urls) =>
        Integration.Invoke("publishObservation", Arguments(("context", Writer), ("observation", new JsonObject
        {
            ["state"] = state, ["healthy"] = healthy,
            ["urls"] = new JsonArray(urls.Select(url => (JsonNode?)JsonValue.Create(url)).ToArray())
        })));

    public JsonNode Read() => Result(Observer.Invoke("readResourceObservations", Arguments(("context", Reader))));

    public void Dispose()
    {
        Integration.Dispose();
        Observer.Dispose();
        Author.Dispose();
        _server.Close();
    }
}
