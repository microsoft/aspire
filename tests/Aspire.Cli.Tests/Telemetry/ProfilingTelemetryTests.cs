// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryTests
{
    [Fact]
    public void StartRunCommand_ReturnsInactiveScopeWhenProfilingIsDisabled()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration());

        using var activity = profilingTelemetry.StartRunCommand();

        Assert.False(activity.IsRunning);
        Assert.Null(startedActivity);
    }

    [Fact]
    public void StartRunCommand_UsesDedicatedProfilingActivitySource()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));

        using var activity = profilingTelemetry.StartRunCommand();

        Assert.True(activity.IsRunning);
        Assert.NotNull(startedActivity);
        Assert.Equal(ProfilingTelemetry.ActivitySourceName, startedActivity.Source.Name);
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.LegacyStartupOperationId));
        Assert.Equal("session-1", startedActivity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
    }

    [Fact]
    public void ProcessSpansUseConsistentExecutableAndArgumentTags()
    {
        var startedActivities = new List<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        var aspirePath = Path.Combine("tools", "aspire");
        var npmPath = Path.Combine("node", "npm");
        var workingDirectory = Directory.GetCurrentDirectory();

        using (profilingTelemetry.StartDetachedSpawnChild(aspirePath, ["run", "--project", "AppHost"], childCommand: "run"))
        {
        }

        using (profilingTelemetry.StartNpmCommand(npmPath, ["exec", "--", "tsx", "apphost.ts"], workingDirectory))
        {
        }

        using (var dotnetActivity = profilingTelemetry.StartDotNetProcess("run", null, new DirectoryInfo(workingDirectory), new ProcessInvocationOptions()))
        {
            dotnetActivity.SetDotNetResolvedExecutable("dotnet", ["run", "--project", "AppHost"], msBuildServer: null);
        }

        using (profilingTelemetry.StartGitCommand("ls-files", "git", ["ls-files", "--cached"], new DirectoryInfo(workingDirectory)))
        {
        }

        Assert.Collection(
            startedActivities,
            spawnActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.Process, spawnActivity.OperationName);
                Assert.Equal("process aspire", spawnActivity.DisplayName);
                Assert.Equal("aspire", spawnActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
                Assert.Equal(aspirePath, spawnActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
                Assert.Equal(new[] { "run", "--project", "AppHost" }, Assert.IsType<string[]>(spawnActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(3, spawnActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
            },
            npmActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.Process, npmActivity.OperationName);
                Assert.Equal("process npm", npmActivity.DisplayName);
                Assert.Equal("npm", npmActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
                Assert.Equal(npmPath, npmActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
                Assert.Equal(new[] { "exec", "--", "tsx", "apphost.ts" }, Assert.IsType<string[]>(npmActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(4, npmActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
            },
            dotnetActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.Process, dotnetActivity.OperationName);
                Assert.Equal("process dotnet", dotnetActivity.DisplayName);
                Assert.Equal("dotnet", dotnetActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
                Assert.Equal("dotnet", dotnetActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
                Assert.Equal(new[] { "run", "--project", "AppHost" }, Assert.IsType<string[]>(dotnetActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(3, dotnetActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
            },
            gitActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.Process, gitActivity.OperationName);
                Assert.Equal("process git", gitActivity.DisplayName);
                Assert.Equal("git", gitActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutableName));
                Assert.Equal("git", gitActivity.GetTagItem(TelemetryConstants.Tags.ProcessExecutablePath));
                Assert.Equal(new[] { "ls-files", "--cached" }, Assert.IsType<string[]>(gitActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgs)));
                Assert.Equal(2, gitActivity.GetTagItem(ProfilingTelemetry.Tags.ProcessCommandArgsCount));
            });
    }

    [Fact]
    public void ProfilingSpansReuseSessionFromAmbientActivityBaggage()
    {
        var startedActivities = new List<Activity>();
        using var parentListener = CreateActivityListener("test-parent", _ => { });
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var parentSource = new ActivitySource("test-parent");
        using var parentActivity = parentSource.StartActivity("parent");
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        Assert.NotNull(parentActivity);

        parentActivity.SetBaggage(ProfilingTelemetry.Baggage.SessionId, "session-1");

        using (profilingTelemetry.StartDetachedSpawnChild("aspire", ["run"], childCommand: "start"))
        {
        }

        using (profilingTelemetry.StartDetachedWaitForBackchannel(childProcessId: 1, expectedHash: "hash", hasLegacyHash: false))
        {
        }

        Assert.Equal(2, startedActivities.Count);
        Assert.All(startedActivities, activity =>
        {
            Assert.Equal("session-1", activity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
            Assert.Equal("session-1", activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        });
    }

    [Fact]
    public void ProfilingSpansStoreGeneratedSessionOnAmbientAncestorsForSiblings()
    {
        var startedActivities = new List<Activity>();
        using var parentListener = CreateActivityListener("test-parent", _ => { });
        using var diagnosticListener = CreateActivityListener("test-diagnostic", _ => { });
        using var listener = CreateProfilingActivityListener(startedActivities.Add);
        using var parentSource = new ActivitySource("test-parent");
        using var diagnosticSource = new ActivitySource("test-diagnostic");
        using var parentActivity = parentSource.StartActivity("parent");
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        Assert.NotNull(parentActivity);

        using (diagnosticSource.StartActivity("diagnostic"))
        {
            using (profilingTelemetry.StartDetachedSpawnChild("aspire", ["run"], childCommand: "start"))
            {
            }
        }

        using (profilingTelemetry.StartDetachedWaitForBackchannel(childProcessId: 1, expectedHash: "hash", hasLegacyHash: false))
        {
        }

        Assert.Equal(2, startedActivities.Count);
        var sessionId = parentActivity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId);
        Assert.NotNull(sessionId);
        Assert.All(startedActivities, activity =>
        {
            Assert.Equal(sessionId, activity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
            Assert.Equal(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        });
    }

    [Fact]
    public void BackchannelTraceContextCarriesActivityBaggage()
    {
        using var listener = CreateProfilingActivityListener(_ => { });
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));

        using var activity = profilingTelemetry.StartJsonRpcClientCall("aux", "GetCapabilitiesAsync", streaming: false);
        var traceContext = activity.CreateBackchannelTraceContext();

        Assert.NotNull(traceContext);
        Assert.Equal("session-1", traceContext.Baggage[ProfilingTelemetry.Baggage.SessionId]);
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        return CreateActivityListener(ProfilingTelemetry.ActivitySourceName, activityStarted);
    }

    private static ActivityListener CreateActivityListener(string sourceName, Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
