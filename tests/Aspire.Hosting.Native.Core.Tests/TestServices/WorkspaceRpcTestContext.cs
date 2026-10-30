// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Exercises revisions across independent author, integration, and observer connections.</summary>
internal sealed class WorkspaceRpcTestContext : IDisposable
{
    public NativeApplicationServer Server { get; }
    public RpcTestClient Author { get; private set; }
    public RpcTestClient Integration { get; }
    public RpcTestClient Observer { get; }
    public JsonNode Workspace { get; private set; }
    public JsonNode Composition { get; }
    public JsonNode Resource { get; }
    public JsonNode Execution { get; }
    public JsonNode Writer { get; }
    public JsonNode Reader { get; }

    public WorkspaceRpcTestContext(IWorkloadExecutor executor)
    {
        Server = new(executor);
        Author = new(Server);
        Integration = new(Server);
        Observer = new(Server);
        Workspace = Result(Author.Invoke("createApplicationWorkspace",
            Arguments(("context", Result(Author.Call("getApplicationServer", null))))));
        Composition = BeginRevision();
        Resource = Author.AddResource(Composition, "cache");
        EnsureSuccess(Author.Invoke("commitRevision", Arguments(("context", Workspace), ("revision", Composition))));
        Execution = Result(Author.Invoke("getApplicationExecution", Arguments(("context", Workspace))));
        Writer = Result(Integration.Invoke("joinResourceExecution", Arguments(
            ("context", Result(Integration.Call("getApplicationServer", null))),
            ("invitation", Result(Author.Invoke("inviteResourceExecution",
                Arguments(("context", Execution), ("resource", Resource))))))));
        Reader = Result(Observer.Invoke("joinApplicationObserver", Arguments(
            ("context", Result(Observer.Call("getApplicationServer", null))),
            ("invitation", Result(Author.Invoke("inviteApplicationObserver", Arguments(("context", Execution))))))));
    }

    public JsonNode BeginRevision() => Result(Author.Invoke("beginRevision", Arguments(("context", Workspace))));

    public void ReconnectAuthor()
    {
        var invitation = Result(Author.Invoke("inviteApplicationWorkspace", Arguments(("context", Workspace))));
        Author.Dispose();
        Author = new(Server);
        Workspace = Result(Author.Invoke("joinApplicationWorkspace",
            Arguments(("context", Result(Author.Call("getApplicationServer", null))), ("invitation", invitation))));
    }

    public JsonObject PublishHealthy() => Integration.Invoke("publishObservation",
        Arguments(("context", Writer), ("observation", new JsonObject
        {
            ["state"] = "Running", ["healthy"] = true, ["urls"] = new JsonArray("tcp://127.0.0.1:5050")
        })));

    public JsonNode Read() => Result(Observer.Invoke("readResourceObservations", Arguments(("context", Reader))));

    public static void EnsureSuccess(JsonObject response)
    {
        if (response["error"] is not null || response["result"] is JsonObject result && result.ContainsKey("$error"))
        {
            throw new InvalidOperationException($"Workspace RPC failed: {response.ToJsonString()}");
        }
    }

    public void Dispose()
    {
        Integration.Dispose();
        Observer.Dispose();
        Author.Dispose();
        Server.Close();
    }
}
