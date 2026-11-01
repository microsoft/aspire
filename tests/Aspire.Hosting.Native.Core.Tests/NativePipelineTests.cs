// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativePipelineTests
{
    private const string BranchingGraph = """
        {"steps":[
          {"name":"release","description":"Custom release target","tags":["release"]},
          {"name":"left","dependsOn":["restore"],"requiredBy":["release"],"tags":["build-compute"],"resource":"web"},
          {"name":"right","dependsOn":["restore"],"requiredBy":["release"]},
          {"name":"restore"},
          {"name":"unrelated"}
        ]}
        """;

    [Fact]
    public async Task InspectionSelectsTransitiveDependenciesAndPreservesMetadataWithoutClaimingActions()
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        var inspection = host.Rpc.InvokeAsync<GetPipelineStepsResponse>("GetPipelineStepsAsync",
            new GetPipelineStepsRequest { Step = "release" });
        server.ConfigurePublishing(BranchingGraph);
        var result = await inspection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Collection(result.Steps,
            step => Assert.Equal("restore", step.Name),
            step =>
            {
                Assert.Equal("left", step.Name);
                Assert.Equal(["restore"], step.DependsOn);
                Assert.Equal(["build-compute"], step.Tags);
                Assert.Equal("web", step.ResourceName);
            },
            step => Assert.Equal("right", step.Name),
            step =>
            {
                Assert.Equal("release", step.Name);
                Assert.Equal(["left", "right"], step.DependsOn);
                Assert.Equal("Custom release target", step.Description);
                Assert.Equal(["release"], step.Tags);
            });
        server.BeginPublishing("release");
        Assert.Equal(["restore"], server.ClaimPublishingSteps());
    }

    [Fact]
    public void NativeSchedulerClaimsIndependentStepsOnceAndAllowsReorderedCompletion()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing(BranchingGraph);
        server.BeginPublishing("release");
        Assert.Equal(["restore"], server.ClaimPublishingSteps());
        Assert.Empty(server.ClaimPublishingSteps());
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingActivity("left", "Too early", true, false));
        server.ReportPublishingActivity("restore", "Restored", true, false);
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingActivity("restore", "Duplicate", true, false));
        Assert.Equal(["left", "right"], server.ClaimPublishingSteps());
        server.ReportPublishingActivity("right", "Built", true, false);
        Assert.Empty(server.ClaimPublishingSteps());
        server.ReportPublishingActivity("left", "Built", true, false);
        Assert.Equal(["release"], server.ClaimPublishingSteps());
        server.ReportPublishingActivity("release", "Released", true, false);
        Assert.Empty(server.ClaimPublishingSteps());
        server.CompletePublishing(false);
        Assert.Throws<InvalidOperationException>(() => server.CompletePublishing(false));
    }

    [Fact]
    public async Task ConcurrentClaimsCannotClaimTheSameNativeStepTwice()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing(BranchingGraph);
        server.BeginPublishing("release");
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(server.ClaimPublishingSteps)));
        Assert.Equal(["restore"], claims.SelectMany(claim => claim).ToArray());
    }

    [Fact]
    public void FailureStopsNewClaimsButRequiresRunningPeersToSettle()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing(BranchingGraph);
        server.BeginPublishing("release");
        server.ClaimPublishingSteps();
        server.ReportPublishingActivity("restore", "Restored", true, false);
        Assert.Equal(["left", "right"], server.ClaimPublishingSteps());
        server.ReportPublishingActivity("right", "Failed", true, true);
        Assert.Empty(server.ClaimPublishingSteps());
        Assert.Throws<InvalidOperationException>(() => server.CompletePublishing(true));
        server.ReportPublishingActivity("left", "Canceled", true, true);
        Assert.Throws<InvalidOperationException>(() => server.CompletePublishing(false));
        server.CompletePublishing(true);
    }

    [Fact]
    public void SuccessRequiresAllSelectedStepsToCompleteAndStopPreventsNewClaims()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing(BranchingGraph);
        Assert.Throws<InvalidOperationException>(() => server.CompletePublishing(false));
        Assert.Throws<ArgumentException>(() => server.BeginPublishing("unknown"));
        server.BeginPublishing("release");
        Assert.Throws<InvalidOperationException>(() => server.CompletePublishing(false));
        Assert.Throws<InvalidOperationException>(() => server.BeginPublishing("release"));
        server.Publishing.Stop();
        Assert.Empty(server.ClaimPublishingSteps());
        server.CompletePublishing(true);
    }

    [Theory]
    [InlineData("""{"steps":[{"name":"same"},{"name":"same"}]}""", "Duplicate")]
    [InlineData("""{"steps":[{"name":"one","dependsOn":["missing"]}]}""", "missing")]
    [InlineData("""{"steps":[{"name":"one","requiredBy":["missing"]}]}""", "missing")]
    [InlineData("""{"steps":[{"name":"one","dependsOn":["two"]},{"name":"two","dependsOn":["one"]}]}""", "cycle")]
    [InlineData("""{"steps":[{"name":"one","requiredBy":["one"]}]}""", "cycle")]
    [InlineData("""{"steps":[{"name":"one","tags":[42]}]}""", "string")]
    [InlineData("""{"steps":[{"name":"one","ignored":true}]}""", "Unknown")]
    [InlineData("""{"steps":[],"steps":[]}""", "duplicate")]
    [InlineData("""{"mode":"unknown","steps":[]}""", "mode")]
    public void InvalidGraphsFailBeforeExecution(string graph, string expected)
    {
        var server = new NativeApplicationServer();
        var exception = Assert.Throws<ArgumentException>(() => server.ConfigurePublishing(graph));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.Publishing.IsConfigured);
    }

    [Fact]
    public async Task ProgressPreservesTaskOwnershipSummaryWarningsAndCompletion()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing("""{"steps":[{"name":"release"}]}""");
        server.BeginPublishing("release");
        server.ClaimPublishingSteps();
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingProgress("release", "task", "early", "Done", true, false, false));
        server.ReportPublishingProgress("release", "task", "upload", "Uploading", false, false, false);
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingActivity("release", "Done", true, false));
        server.ReportPublishingProgress("release", "task", "upload", "Halfway", false, false, false);
        server.ReportPublishingProgress("release", "task-warning", "upload", "Uploaded with warning", true, false, true);
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingProgress("release", "task", "upload", "Again", false, false, false));
        server.ReportPublishingProgress("release", "log-debug", "entry", "Safe log", false, false, false);
        server.ReportPublishingProgress("release", "summary", "Result", "**Ready**", false, false, true);
        server.ReportPublishingActivity("release", "Done", true, false);
        server.CompletePublishing(false);
        var activities = new List<NativePublishingActivity>();
        await foreach (var activity in server.Publishing.ReadAsync(TestContext.Current.CancellationToken))
        {
            activities.Add(activity);
        }
        Assert.Collection(activities,
            activity => Assert.Equal("step", activity.Type),
            activity =>
            {
                Assert.Equal("task", activity.Type);
                Assert.Equal("release", activity.Data.StepId);
                Assert.Equal("upload", activity.Data.Id);
            },
            activity => Assert.Equal("Halfway", activity.Data.StatusText),
            activity => Assert.Equal("CompletedWithWarning", activity.Data.CompletionState),
            activity => Assert.Equal("Debug", activity.Data.LogLevel),
            activity => Assert.Equal("CompletedWithWarning", activity.Data.CompletionState),
            activity =>
            {
                Assert.Equal("publish-complete", activity.Type);
                var summary = Assert.IsType<NativePipelineSummaryItem[]>(activity.Data.PipelineSummary);
                Assert.Equal([new NativePipelineSummaryItem("Result", "**Ready**", true)], summary);
            });
    }

    [Fact]
    public void FailedTasksPreventSilentStepSuccess()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing("""{"steps":[{"name":"release"}]}""");
        server.BeginPublishing("release");
        server.ClaimPublishingSteps();
        server.ReportPublishingProgress("release", "task", "upload", "Uploading", false, false, false);
        server.ReportPublishingProgress("release", "task", "upload", "Failed", true, true, false);
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingActivity("release", "Done", true, false));
        server.ReportPublishingActivity("release", "Failed", true, true);
        server.CompletePublishing(true);
    }

    [Fact]
    public async Task VerboseGuestProgressCannotConsumeTerminalActivityCapacity()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing("""{"steps":[{"name":"release"}]}""");
        server.BeginPublishing("release");
        server.ClaimPublishingSteps();
        for (var index = 0; index < 4096; index++)
        {
            server.ReportPublishingProgress("release", "log", "entry", "Bounded log", false, false, false);
        }
        Assert.Throws<InvalidOperationException>(() => server.ReportPublishingProgress("release", "log", "entry", "Overflow", false, false, false));
        server.ReportPublishingActivity("release", "Failed", true, true);
        server.CompletePublishing(true);
        NativePublishingActivity? last = null;
        var count = 0;
        await foreach (var activity in server.Publishing.ReadAsync(TestContext.Current.CancellationToken))
        {
            last = activity;
            count++;
        }
        Assert.Equal(4099, count);
        Assert.NotNull(last);
        Assert.Equal("publish-complete", last.Type);
        Assert.True(last.Data.IsError);
    }

    [Fact]
    public async Task RunModeStartupPipelinePreservesOrdinaryCliStopAfterAcknowledgingTheResponse()
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        server.ConfigurePublishing("""{"mode":"run","steps":[{"name":"before-start"}]}""");
        Assert.False(server.Publishing.WaitsForStop);
        server.BeginPublishing("before-start");
        Assert.Equal(["before-start"], server.ClaimPublishingSteps());
        server.ReportPublishingActivity("before-start", "Startup configured", true, false);
        server.CompletePublishing(false);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Rpc.Disconnected += (_, _) => disconnected.TrySetResult();
        await host.Rpc.InvokeAsync("RequestStopAsync");
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // A publication acknowledgement leaves the CLI connection alive and signals this task.
        // Run-mode startup must instead invoke the normal host lifetime stop callback.
        Assert.False(server.WaitForPublishingStop().IsCompleted);
    }

    [Fact]
    public async Task CompletedRunGraphsCanBeReplacedWithoutRetainingUndrainedPublishingActivities()
    {
        var server = new NativeApplicationServer();
        const string graph = """{"mode":"run","steps":[{"name":"before-start"}]}""";
        for (var generation = 0; generation < 2; generation++)
        {
            server.ConfigurePublishing(graph);
            Assert.Throws<InvalidOperationException>(() => server.ConfigurePublishing(graph));
            server.BeginPublishing("before-start");
            Assert.Equal(["before-start"], server.ClaimPublishingSteps());
            for (var index = 0; index < 5000; index++)
            {
                server.ReportPublishingProgress("before-start", "log", "entry", "Guest console diagnostic", false, false, false);
            }
            server.ReportPublishingActivity("before-start", "Startup ready", true, false);
            server.CompletePublishing(false);
            var activityCount = 0;
            await foreach (var activity in server.Publishing.ReadAsync(TestContext.Current.CancellationToken))
            {
                activityCount++;
            }
            Assert.Equal(0, activityCount);
        }
        Assert.Throws<InvalidOperationException>(() => server.ConfigurePublishing("""{"mode":"publish","steps":[]}"""));
    }

    [Fact]
    public void CompletedPublishingGraphsRemainSealed()
    {
        var server = new NativeApplicationServer();
        server.ConfigurePublishing("""{"steps":[{"name":"publish"}]}""");
        server.BeginPublishing("publish");
        server.ClaimPublishingSteps();
        server.ReportPublishingActivity("publish", "Done", true, false);
        server.CompletePublishing(false);
        Assert.Throws<InvalidOperationException>(() => server.ConfigurePublishing("""{"mode":"run","steps":[]}"""));
        Assert.Throws<InvalidOperationException>(() => server.ConfigurePublishing("""{"mode":"publish","steps":[]}"""));
    }
}
