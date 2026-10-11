// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class RuntimeRpcTests
{
    [Fact]
    public async Task IndependentConnectionsPublishAndObserveWithoutBlockingDispatch()
    {
        using var context = new RuntimeRpcTestContext();
        var initial = context.Read();
        var wait = context.Observer.InvokeAsync("waitResourceObservations", Arguments(
            ("context", context.Reader), ("version", initial["version"]), ("timeoutMilliseconds", JsonValue.Create(30000))));
        Assert.False(wait.IsCompleted);
        Assert.Empty(Result(context.Observer.Invoke("readConfirmations", Arguments(("context", context.Reader))))["requests"]!.AsArray());
        Assert.Null(context.Publish("Running", healthy: true, "http://localhost:8000")["result"]);
        var updated = Result(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(updated["version"]!.GetValue<long>() > initial["version"]!.GetValue<long>());
        var resource = Assert.Single(updated["resources"]!.AsArray())!;
        Assert.Equal("cache", resource["name"]!.GetValue<string>());
        Assert.Equal("Running", resource["state"]!.GetValue<string>());
        Assert.True(resource["healthy"]!.GetValue<bool>());
        Assert.Equal("http://localhost:8000", Assert.Single(resource["urls"]!.AsArray())!.GetValue<string>());
        Assert.Equal("TYPE_MISMATCH", Error(context.Observer.Invoke("publishObservation", Arguments(
            ("context", context.Reader), ("observation", new JsonObject { ["state"] = "Running", ["healthy"] = true, ["urls"] = new JsonArray() })))));
    }

    [Fact]
    public void InvitationsAreOneUseRoleScopedAndRetiredWithTheirGeneration()
    {
        using var context = new RuntimeRpcTestContext();
        var root = Result(context.Observer.Call("getApplicationServer", null));
        var invite = Result(context.Author.Invoke("inviteApplicationObserver", Arguments(("context", context.Execution))));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Observer.Invoke("joinResourceExecution", Arguments(
            ("context", root), ("invitation", invite)))));
        Assert.NotNull(Result(context.Observer.Invoke("joinApplicationObserver", Arguments(
            ("context", root), ("invitation", invite)))));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Observer.Invoke("joinApplicationObserver", Arguments(
            ("context", root), ("invitation", invite)))));
        var stale = Result(context.Author.Invoke("inviteApplicationObserver", Arguments(("context", context.Execution))));
        context.Author.Invoke("retireGeneration", Arguments(("context", context.Session), ("composition", context.Composition)));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Observer.Invoke("joinApplicationObserver", Arguments(
            ("context", root), ("invitation", stale)))));
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Observer.Invoke("readResourceObservations", Arguments(("context", context.Reader)))));
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Publish("Running", healthy: true)));
    }

    [Fact]
    public void LogHistoryHasOrderedCursorsAndExplicitTruncation()
    {
        using var context = new RuntimeRpcTestContext();
        for (var index = 1; index <= 300; index++)
        {
            context.Integration.Invoke("appendResourceLog", Arguments(("context", context.Writer),
                ("stream", JsonValue.Create("stdout")), ("message", JsonValue.Create($"entry-{index}"))));
        }
        var id = context.Read()["resources"]![0]!["resourceId"];
        var logs = Result(context.Observer.Invoke("readResourceLogs", Arguments(("context", context.Reader),
            ("resourceId", id), ("afterSequence", JsonValue.Create(0)))));
        Assert.True(logs["truncated"]!.GetValue<bool>());
        Assert.Equal(300, logs["lastSequence"]!.GetValue<long>());
        Assert.Equal(Enumerable.Range(45, 256).Select(index => $"entry-{index}"),
            logs["entries"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>()));
        var suffix = Result(context.Observer.Invoke("readResourceLogs", Arguments(("context", context.Reader),
            ("resourceId", id), ("afterSequence", JsonValue.Create(298)))));
        Assert.False(suffix["truncated"]!.GetValue<bool>());
        Assert.Equal([299L, 300L], suffix["entries"]!.AsArray().Select(entry => entry!["sequence"]!.GetValue<long>()));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Observer.Invoke("readResourceLogs", Arguments(("context", context.Reader),
            ("resourceId", id), ("afterSequence", JsonValue.Create(301))))));
    }

    [Fact]
    public async Task CommandsAndConfirmationsRoundTripAcrossOwners()
    {
        using var context = new RuntimeRpcTestContext();
        context.Integration.Invoke("defineResourceCommand", Arguments(("context", context.Writer),
            ("name", JsonValue.Create("refresh")), ("displayName", JsonValue.Create("Refresh"))));
        var command = Result(context.Observer.Invoke("invokeResourceCommand", Arguments(("context", context.Reader),
            ("resourceName", JsonValue.Create("cache")), ("commandName", JsonValue.Create("refresh")))));
        var completion = context.Observer.InvokeAsync("awaitRuntimeOperation", Arguments(("context", command)));
        Assert.False(completion.IsCompleted);
        var pending = Result(context.Integration.Invoke("readResourceCommands", Arguments(("context", context.Writer))))["commands"]![0]!;
        Assert.Equal("refresh", pending["name"]!.GetValue<string>());
        context.Integration.Invoke("completeResourceCommand", Arguments(("context", context.Writer),
            ("requestId", pending["requestId"]), ("result", new JsonObject { ["status"] = "succeeded", ["message"] = "refreshed" })));
        var result = Result(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("succeeded", result["status"]!.GetValue<string>());
        Assert.Equal("refreshed", result["message"]!.GetValue<string>());
        var confirmation = Result(context.Integration.Invoke("requestConfirmation", Arguments(("context", context.Writer),
            ("message", JsonValue.Create("Continue?")))));
        var answer = context.Integration.InvokeAsync("awaitRuntimeOperation", Arguments(("context", confirmation)));
        var prompt = Assert.Single(Result(context.Observer.Invoke("readConfirmations", Arguments(("context", context.Reader))))["requests"]!.AsArray())!;
        Assert.Equal("cache", prompt["resourceName"]!.GetValue<string>());
        Assert.Equal("Continue?", prompt["message"]!.GetValue<string>());
        context.Observer.Invoke("respondConfirmation", Arguments(("context", context.Reader),
            ("requestId", prompt["requestId"]), ("accepted", JsonValue.Create(true))));
        Assert.Equal("accepted", Result(await answer.WaitAsync(TimeSpan.FromSeconds(10)))["status"]!.GetValue<string>());
        Assert.Equal("INVALID_ARGUMENT", Error(context.Observer.Invoke("respondConfirmation", Arguments(("context", context.Reader),
            ("requestId", prompt["requestId"]), ("accepted", JsonValue.Create(true))))));
    }

    [Fact]
    public async Task IntegrationDisconnectFailsResourceAndCancelsOutstandingCommands()
    {
        using var context = new RuntimeRpcTestContext();
        context.Publish("Running", healthy: true, "http://localhost:8000");
        context.Integration.Invoke("defineResourceCommand", Arguments(("context", context.Writer),
            ("name", JsonValue.Create("refresh")), ("displayName", JsonValue.Create("Refresh"))));
        var command = Result(context.Observer.Invoke("invokeResourceCommand", Arguments(("context", context.Reader),
            ("resourceName", JsonValue.Create("cache")), ("commandName", JsonValue.Create("refresh")))));
        var result = context.Observer.InvokeAsync("awaitRuntimeOperation", Arguments(("context", command)));
        context.Integration.Dispose();
        Assert.Equal("cancelled", Result(await result.WaitAsync(TimeSpan.FromSeconds(10)))["status"]!.GetValue<string>());
        var resource = context.Read()["resources"]![0]!;
        Assert.Equal("Failed", resource["state"]!.GetValue<string>());
        Assert.False(resource["healthy"]!.GetValue<bool>());
        Assert.Empty(resource["urls"]!.AsArray());
        Assert.Empty(resource["commands"]!.AsArray());
    }

    [Fact]
    public async Task AuthorRetirementUnblocksPendingInteractionsAndRejectsLateWrites()
    {
        using var context = new RuntimeRpcTestContext();
        var prompt = Result(context.Integration.Invoke("requestConfirmation", Arguments(("context", context.Writer),
            ("message", JsonValue.Create("Continue?")))));
        var answer = context.Integration.InvokeAsync("awaitRuntimeOperation", Arguments(("context", prompt)));
        context.Author.Dispose();
        Assert.Equal("cancelled", Result(await answer.WaitAsync(TimeSpan.FromSeconds(10)))["status"]!.GetValue<string>());
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Publish("Running", healthy: true)));
    }

    [Theory]
    [InlineData("{\"state\":\"Running\",\"healthy\":true}")]
    [InlineData("{\"state\":null,\"healthy\":true,\"urls\":[]}")]
    [InlineData("{\"state\":\"Running\",\"healthy\":true,\"urls\":[null]}")]
    [InlineData("{\"state\":\"Running\",\"healthy\":true,\"urls\":[],\"unexpected\":true}")]
    [InlineData("{\"state\":\"Failed\",\"healthy\":true,\"urls\":[]}")]
    [InlineData("{\"state\":\"Running\",\"healthy\":true,\"urls\":[\"http://user:secret@localhost\"]}")]
    public void MalformedObservationsCannotMutateRuntimeState(string observation)
    {
        using var context = new RuntimeRpcTestContext();
        Assert.Equal("INVALID_ARGUMENT", Error(context.Integration.Invoke("publishObservation", Arguments(
            ("context", context.Writer), ("observation", JsonNode.Parse(observation))))));
        Assert.Equal("Unknown", context.Read()["resources"]![0]!["state"]!.GetValue<string>());
    }

    private static string Error(JsonObject response) =>
        (response["result"]?["$error"]?["code"] ?? response["error"]!["data"]!["code"])!.GetValue<string>();
}
