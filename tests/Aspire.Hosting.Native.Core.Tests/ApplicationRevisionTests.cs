// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;
using static Aspire.Hosting.Native.Core.Tests.TestServices.WorkspaceRpcTestContext;

namespace Aspire.Hosting.Native.Core.Tests;

public class ApplicationRevisionTests
{
    [Fact]
    public async Task IdenticalRevisionRetainsWorkloadOwnerLogsAndCommandsButRevokesOldAuthoringHandles()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        EnsureSuccess(context.PublishHealthy());
        EnsureSuccess(context.Integration.Invoke("appendResourceLog",
            Arguments(("context", context.Writer), ("stream", JsonValue.Create("stdout")), ("message", JsonValue.Create("retained")))));
        EnsureSuccess(context.Integration.Invoke("defineResourceCommand",
            Arguments(("context", context.Writer), ("name", JsonValue.Create("ping")), ("displayName", JsonValue.Create("Ping")))));
        var original = context.Read();
        var revision = context.BeginRevision();
        var resource = context.Author.AddResource(revision, "cache");
        EnsureSuccess(await Commit(context, revision));
        var current = context.Read();
        Assert.Equal(original["generationId"]!.GetValue<string>(), current["generationId"]!.GetValue<string>());
        Assert.Equal(original["resources"]!.ToJsonString(), current["resources"]!.ToJsonString());
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("inspect", Arguments(("context", context.Composition)))));
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("inviteResourceExecution",
            Arguments(("context", context.Execution), ("resource", context.Resource)))));
        EnsureSuccess(context.PublishHealthy());
        var resourceId = current["resources"]![0]!["resourceId"];
        var logs = Result(context.Observer.Invoke("readResourceLogs",
            Arguments(("context", context.Reader), ("resourceId", resourceId), ("afterSequence", JsonValue.Create(0)))));
        Assert.Equal("retained", logs["entries"]![0]!["message"]!.GetValue<string>());
        var execution = Result(context.Author.Invoke("getApplicationExecution", Arguments(("context", context.Workspace))));
        var invitation = Result(context.Author.Invoke("inviteResourceExecution",
            Arguments(("context", execution), ("resource", resource))));
        Assert.Equal("OPERATION_REJECTED", Error(context.Integration.Invoke("joinResourceExecution",
            Arguments(("context", Result(context.Integration.Call("getApplicationServer", null))), ("invitation", invitation)))));
        Assert.Single(executor.Started);
        Assert.Empty(executor.Removed);
    }

    [Fact]
    public async Task IdenticalRevisionPreservesPendingCommandsConfirmationsAndObserverAuthority()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        EnsureSuccess(context.PublishHealthy());
        EnsureSuccess(context.Integration.Invoke("defineResourceCommand", Arguments(
            ("context", context.Writer), ("name", JsonValue.Create("ping")), ("displayName", JsonValue.Create("Ping")))));
        var command = Result(context.Observer.Invoke("invokeResourceCommand", Arguments(
            ("context", context.Reader), ("resourceName", JsonValue.Create("cache")), ("commandName", JsonValue.Create("ping")))));
        var confirmation = Result(context.Integration.Invoke("requestConfirmation", Arguments(
            ("context", context.Writer), ("message", JsonValue.Create("Keep running?")))));
        var commandResult = context.Observer.InvokeAsync("awaitRuntimeOperation", Arguments(("context", command)));
        var confirmationResult = context.Integration.InvokeAsync("awaitRuntimeOperation", Arguments(("context", confirmation)));
        var commands = Result(context.Integration.Invoke("readResourceCommands", Arguments(("context", context.Writer))));
        var confirmations = Result(context.Observer.Invoke("readConfirmations", Arguments(("context", context.Reader))));
        context.ReconnectAuthor();
        var revision = context.BeginRevision();
        context.Author.AddResource(revision, "cache");
        EnsureSuccess(await Commit(context, revision));
        Assert.Equal(commands.ToJsonString(), Result(context.Integration.Invoke("readResourceCommands",
            Arguments(("context", context.Writer)))).ToJsonString());
        Assert.Equal(confirmations.ToJsonString(), Result(context.Observer.Invoke("readConfirmations",
            Arguments(("context", context.Reader)))).ToJsonString());
        EnsureSuccess(context.Integration.Invoke("completeResourceCommand", Arguments(
            ("context", context.Writer), ("requestId", commands["commands"]![0]!["requestId"]),
            ("result", new JsonObject { ["status"] = "succeeded", ["message"] = "pong" }))));
        EnsureSuccess(context.Observer.Invoke("respondConfirmation", Arguments(
            ("context", context.Reader), ("requestId", confirmations["requests"]![0]!["requestId"]),
            ("accepted", JsonValue.Create(true)))));
        Assert.Equal("pong", Result(await commandResult)["message"]!.GetValue<string>());
        Assert.Equal("accepted", Result(await confirmationResult)["status"]!.GetValue<string>());
        Assert.Single(context.Read()["resources"]!.AsArray());
    }

    [Fact]
    public async Task AuthorDisconnectAbortsStagingWhileWorkspaceAndIntegrationSurviveReconnection()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        EnsureSuccess(context.PublishHealthy());
        var original = context.Read();
        var abandoned = context.BeginRevision();
        context.Author.AddResource(abandoned, "unfinished");
        context.ReconnectAuthor();
        Assert.Equal(original.ToJsonString(), context.Read().ToJsonString());
        var revision = context.BeginRevision();
        context.Author.AddResource(revision, "cache");
        EnsureSuccess(await Commit(context, revision));
        EnsureSuccess(context.PublishHealthy());
        Assert.Single(executor.Started);
        Assert.Empty(executor.Removed);
    }

    [Fact]
    public void AbortAndInvalidCompositionLeaveTheCommittedModelUntouched()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        EnsureSuccess(context.PublishHealthy());
        var original = context.Read();
        var revision = context.BeginRevision();
        var a = context.Author.AddResource(revision, "a");
        var b = context.Author.AddResource(revision, "b");
        EnsureSuccess(context.Author.Invoke("waitFor", Arguments(("context", a), ("dependency", b))));
        Assert.Equal("OPERATION_REJECTED", Error(context.Author.Invoke("waitFor", Arguments(("context", b), ("dependency", a)))));
        Assert.Equal("OPERATION_REJECTED", Error(context.Author.Invoke("beginRevision", Arguments(("context", context.Workspace)))));
        EnsureSuccess(context.Author.Invoke("abortRevision", Arguments(("context", context.Workspace), ("revision", revision))));
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("inspect", Arguments(("context", revision)))));
        Assert.Equal(original.ToJsonString(), context.Read().ToJsonString());
        Assert.NotNull(context.BeginRevision());
    }

    [Fact]
    public async Task RemovalRevokesOwnerImmediatelyAndFencesNextRevisionUntilActualCleanup()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var revision = context.BeginRevision();
        var commit = Commit(context, revision);
        try
        {
            await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(commit.IsCompleted);
            Assert.Equal("HANDLE_NOT_FOUND", Error(context.PublishHealthy()));
            Assert.Empty(context.Read()["resources"]!.AsArray());
            Assert.Equal("OPERATION_REJECTED", Error(context.Author.Invoke("beginRevision", Arguments(("context", context.Workspace)))));
        }
        finally
        {
            executor.ReleaseCleanup();
        }
        EnsureSuccess(await commit);
        Assert.Single(executor.Removed);
        Assert.NotNull(context.BeginRevision());
    }

    [Fact]
    public async Task TypeReplacementGetsNewExecutionIdentityAndRejectsLateWrites()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var id = context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>();
        var revision = context.BeginRevision();
        var replacement = Result(context.Author.Invoke("addResource",
            Arguments(("context", revision), ("name", JsonValue.Create("cache")), ("typeId", JsonValue.Create("other/Resource")))));
        EnsureSuccess(await Commit(context, revision));
        Assert.NotEqual(id, context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>());
        Assert.Single(executor.Removed);
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.PublishHealthy()));
        var execution = Result(context.Author.Invoke("getApplicationExecution", Arguments(("context", context.Workspace))));
        Assert.NotNull(Result(context.Author.Invoke("inviteResourceExecution",
            Arguments(("context", execution), ("resource", replacement)))));
    }

    [Fact]
    public async Task ConfigurationChangesWakeIntegrationAndRequireCurrentAcknowledgementBeforeHealthy()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        EnsureSuccess(context.PublishHealthy());
        var waiting = context.Integration.InvokeAsync("waitResourceConfiguration",
            Arguments(("context", context.Writer), ("revision", JsonValue.Create(1L)), ("timeoutMilliseconds", JsonValue.Create(10000))));
        var revision = context.BeginRevision();
        var resource = context.Author.AddResource(revision, "cache");
        SetConfiguration(context.Author, resource, "new-value");
        EnsureSuccess(await Commit(context, revision));
        var change = Result(await waiting);
        Assert.Equal(2, change["revision"]!.GetValue<long>());
        Assert.Equal(1, change["appliedRevision"]!.GetValue<long>());
        Assert.Equal("pending", change["status"]!.GetValue<string>());
        Assert.Equal("new-value", change["properties"]![0]!["value"]!.GetValue<string>());
        Assert.False(context.Read()["resources"]![0]!["healthy"]!.GetValue<bool>());
        Assert.Equal("OPERATION_REJECTED", Error(context.PublishHealthy()));
        EnsureSuccess(CompleteConfiguration(context, 2, "failed"));
        Assert.Equal("failed", context.Read()["resources"]![0]!["configurationStatus"]!.GetValue<string>());
        var next = context.BeginRevision();
        var nextResource = context.Author.AddResource(next, "cache");
        SetConfiguration(context.Author, nextResource, "retry-value");
        EnsureSuccess(await Commit(context, next));
        Assert.Equal("OPERATION_REJECTED", Error(CompleteConfiguration(context, 2, "succeeded")));
        EnsureSuccess(CompleteConfiguration(context, 3, "succeeded"));
        EnsureSuccess(context.PublishHealthy());
        Assert.Equal(3, context.Read()["resources"]![0]!["appliedConfigurationRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task DependencyRevisionUsesStableExecutionIdsAndMarksDependentConfigurationPending()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        var revision = context.BeginRevision();
        var cache = context.Author.AddResource(revision, "cache");
        var dependency = context.Author.AddResource(revision, "database");
        EnsureSuccess(context.Author.Invoke("waitFor", Arguments(("context", cache), ("dependency", dependency))));
        EnsureSuccess(await Commit(context, revision));
        var configuration = Result(context.Integration.Invoke("readResourceConfiguration", Arguments(("context", context.Writer))));
        var database = context.Read()["resources"]![1]!;
        Assert.Equal(database["resourceId"]!.GetValue<string>(), Assert.Single(configuration["dependencies"]!.AsArray())!.GetValue<string>());
        Assert.Equal(2, configuration["revision"]!.GetValue<long>());
        Assert.Equal("pending", configuration["status"]!.GetValue<string>());
    }

    [Fact]
    public void ForeignRevisionAndWrongRoleInvitationCannotAcquireWorkspaceAuthority()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        using var foreign = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        var revision = foreign.BeginRevision();
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("commitRevision",
            Arguments(("context", context.Workspace), ("revision", revision)))));
        var invitation = Result(context.Author.Invoke("inviteApplicationWorkspace", Arguments(("context", context.Workspace))));
        var root = Result(context.Integration.Call("getApplicationServer", null));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Integration.Invoke("joinResourceExecution",
            Arguments(("context", root), ("invitation", invitation)))));
        Assert.NotNull(Result(context.Integration.Invoke("joinApplicationWorkspace", Arguments(("context", root), ("invitation", invitation)))));
        Assert.Equal("INVALID_ARGUMENT", Error(context.Integration.Invoke("joinApplicationWorkspace",
            Arguments(("context", root), ("invitation", invitation)))));
    }

    [Fact]
    public async Task WorkloadRestartWaitsForCleanupRejectsOverlapAndInvalidatesDependentConfiguration()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var revision = context.BeginRevision();
        var cache = context.Author.AddResource(revision, "cache");
        var dependent = context.Author.AddResource(revision, "tunnel");
        EnsureSuccess(context.Author.Invoke("waitFor", Arguments(("context", dependent), ("dependency", cache))));
        EnsureSuccess(await Commit(context, revision));
        var execution = Result(context.Author.Invoke("getApplicationExecution", Arguments(("context", context.Workspace))));
        var writer = Result(context.Integration.Invoke("joinResourceExecution", Arguments(
            ("context", Result(context.Integration.Call("getApplicationServer", null))),
            ("invitation", Result(context.Author.Invoke("inviteResourceExecution",
                Arguments(("context", execution), ("resource", dependent))))))));
        var restart = context.Integration.InvokeAsync("restartContainer", RestartArguments(context.Writer, 1));
        try
        {
            await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(restart.IsCompleted);
            Assert.Single(executor.Started);
            Assert.Equal("OPERATION_REJECTED", Error(context.Integration.Invoke("restartContainer", RestartArguments(context.Writer, 1))));
        }
        finally
        {
            executor.ReleaseCleanup();
        }
        EnsureSuccess(await restart);
        Assert.Equal(2, executor.Started.Count);
        Assert.Single(executor.Removed);
        var configuration = Result(context.Integration.Invoke("readResourceConfiguration", Arguments(("context", writer))));
        Assert.Equal(2, configuration["revision"]!.GetValue<long>());
        Assert.Equal("pending", configuration["status"]!.GetValue<string>());
        var dependencies = Result(context.Integration.Invoke("readResourceDependencies", Arguments(("context", writer))));
        Assert.Equal("cache", Assert.Single(dependencies["resources"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal("OPERATION_REJECTED", Error(context.Integration.Invoke("restartContainer", RestartArguments(context.Writer, 0))));
    }

    [Fact]
    public async Task CommitDuringReplacementIsRejectedWithoutMutatingModelAndCanRetryAfterCleanup()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var original = context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>();
        var restart = context.Integration.InvokeAsync("restartContainer", RestartArguments(context.Writer, 1));
        var revision = context.BeginRevision();
        var cache = context.Author.AddResource(revision, "cache");
        SetConfiguration(context.Author, cache, "later-value");
        try
        {
            await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("OPERATION_REJECTED", Error(await Commit(context, revision)));
            Assert.Equal(1, context.Read()["resources"]![0]!["configurationRevision"]!.GetValue<long>());
            Assert.Equal(original, context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>());
            Assert.Single(executor.Started);
        }
        finally
        {
            executor.ReleaseCleanup();
        }
        EnsureSuccess(await restart);
        EnsureSuccess(await Commit(context, revision));
        Assert.Equal(2, context.Read()["resources"]![0]!["configurationRevision"]!.GetValue<long>());
        Assert.Equal(2, executor.Started.Count);
    }

    [Fact]
    public async Task RetirementDuringReplacementPreventsResourceResurrection()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var restart = context.Integration.InvokeAsync("restartContainer", RestartArguments(context.Writer, 1));
        await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retire = context.Author.InvokeAsync("retireApplicationWorkspace", Arguments(("context", context.Workspace)));
        try
        {
            Assert.False(retire.IsCompleted);
            Assert.Equal("HANDLE_NOT_FOUND", Error(context.PublishHealthy()));
        }
        finally
        {
            executor.ReleaseCleanup();
        }
        EnsureSuccess(await retire);
        Assert.Equal("HANDLE_NOT_FOUND", Error(await restart));
        Assert.Single(executor.Started);
        Assert.Single(executor.Removed);
    }

    [Fact]
    public async Task RemovingResourceCompletesInFlightConfigurationAndCommandWaitsWithExpiredAuthority()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        var configuration = context.Integration.InvokeAsync("waitResourceConfiguration", Arguments(
            ("context", context.Writer), ("revision", JsonValue.Create(1L)), ("timeoutMilliseconds", JsonValue.Create(10000))));
        var commands = context.Integration.InvokeAsync("waitResourceCommands", Arguments(
            ("context", context.Writer), ("timeoutMilliseconds", JsonValue.Create(10000))));
        EnsureSuccess(await Commit(context, context.BeginRevision()));
        Assert.Equal("HANDLE_NOT_FOUND", Error(await configuration));
        Assert.Equal("HANDLE_NOT_FOUND", Error(await commands));
    }

    [Fact]
    public async Task CleanupFailureIsExplicitAndDoesNotPermitSuccessShapedReplacement()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new WorkspaceRpcTestContext(executor);
        await StartWorkload(context);
        var revision = context.BeginRevision();
        var commit = Commit(context, revision);
        await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        executor.FailCleanup();
        Assert.Equal("OPERATION_REJECTED", Error(await commit));
        Assert.Equal("OPERATION_REJECTED", Error(context.Author.Invoke("beginRevision", Arguments(("context", context.Workspace)))));
        Assert.Empty(executor.Removed);
        Assert.Equal("OPERATION_REJECTED", Error(await context.Author.InvokeAsync("retireApplicationWorkspace",
            Arguments(("context", context.Workspace)))));
    }

    [Fact]
    public async Task RemovingAndReaddingSameNameDoesNotRestoreOldExecutionAuthority()
    {
        using var context = new WorkspaceRpcTestContext(UnavailableWorkloadExecutor.Instance);
        var original = context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>();
        EnsureSuccess(await Commit(context, context.BeginRevision()));
        var revision = context.BeginRevision();
        context.Author.AddResource(revision, "cache");
        EnsureSuccess(await Commit(context, revision));
        Assert.NotEqual(original, context.Read()["resources"]![0]!["resourceId"]!.GetValue<string>());
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.PublishHealthy()));
    }

    private static JsonObject RestartArguments(JsonNode writer, long revision) =>
        Arguments(("context", writer), ("revision", JsonValue.Create(revision)), ("container", new JsonObject
        {
            ["image"] = "test-replacement", ["targetPort"] = 6379, ["environment"] = new JsonArray(), ["arguments"] = new JsonArray()
        }));

    private static async Task StartWorkload(WorkspaceRpcTestContext context)
    {
        EnsureSuccess(await context.Integration.InvokeAsync("startContainer",
            Arguments(("context", context.Writer), ("container", new JsonObject
            {
                ["image"] = "test", ["targetPort"] = 6379, ["environment"] = new JsonArray(), ["arguments"] = new JsonArray()
            }))));
    }

    private static Task<JsonObject> Commit(WorkspaceRpcTestContext context, JsonNode revision) =>
        context.Author.InvokeAsync("commitRevision", Arguments(("context", context.Workspace), ("revision", revision)));

    private static void SetConfiguration(RpcTestClient author, JsonNode resource, string value) =>
        EnsureSuccess(author.Invoke("setResourceConfiguration", Arguments(("context", resource), ("configuration", new JsonObject
        {
            ["properties"] = new JsonArray(new JsonObject { ["name"] = "value", ["value"] = value })
        }))));

    private static JsonObject CompleteConfiguration(WorkspaceRpcTestContext context, long revision, string status) =>
        context.Integration.Invoke("completeResourceConfiguration", Arguments(
            ("context", context.Writer), ("revision", JsonValue.Create(revision)),
            ("result", new JsonObject { ["status"] = status, ["message"] = "" })));

    private static string Error(JsonObject response) =>
        response["error"]?["data"]?["code"]?.GetValue<string>() ??
        response["result"]?["$error"]?["code"]?.GetValue<string>() ??
        throw new InvalidOperationException("The test expected an RPC error.");
}
