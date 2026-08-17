// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Cli.DotNet;
using Aspire.Cli.Telemetry;
using Aspire.Tests;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryTests
{
    [Fact]
    public void StartRunCommand_ReturnsInactiveScopeWhenProfilingIsDisabled()
    {
        Activity? startedActivity = null;
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration());
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: activity => startedActivity = activity);

        using var activity = profilingTelemetry.StartRunCommand();

        Assert.False(activity.IsRunning);
        Assert.Null(startedActivity);
    }

    [Fact]
    public void StartRunCommand_UsesDedicatedProfilingActivitySource()
    {
        Activity? startedActivity = null;
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: activity => startedActivity = activity);

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
        var startedActivities = new ConcurrentQueue<Activity>();
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Enqueue);
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

        var sessionActivities = GetSessionActivities(startedActivities, "session-1");
        Assert.Collection(
            sessionActivities,
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
    public void StartAddCommand_CreatesAddSpecificSpans()
    {
        var startedActivities = new ConcurrentQueue<Activity>();
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Enqueue);
        var appHostProjectFile = new FileInfo(Path.Combine("AppHost", "AppHost.csproj"));

        using (var addActivity = profilingTelemetry.StartAddCommand("redis", "13.4.0", "nuget-source", appHostProjectFile))
        {
            using (var findAppHostActivity = profilingTelemetry.StartAddFindAppHost(appHostProjectFile))
            {
                findAppHostActivity.SetAppHostCandidateCount(1);
            }

            using (var configuredChannelActivity = profilingTelemetry.StartAddGetConfiguredChannel())
            {
                configuredChannelActivity.SetAddConfiguredChannel("daily");
            }

            using (var searchPackagesActivity = profilingTelemetry.StartAddSearchPackages("daily"))
            {
                searchPackagesActivity.SetAddPackageSearchResultCount(42);
            }

            using (var selectPackageActivity = profilingTelemetry.StartAddSelectPackage("redis", "13.4.0"))
            {
                selectPackageActivity.SetAddPackageMatch(1, ProfilingTelemetry.Values.AddPackageMatchKindExact);
                selectPackageActivity.SetAddSelectedPackage("Aspire.Hosting.Redis", "13.4.0", "daily");
            }

            using (profilingTelemetry.StartAddSelectPackagePrompt())
            {
            }

            using (var stopRunningInstanceActivity = profilingTelemetry.StartAddStopExistingInstance())
            {
                stopRunningInstanceActivity.SetAppHostRunningInstanceResult("NoInstanceFound");
            }

            using (var addPackageActivity = profilingTelemetry.StartAddPackage("Aspire.Hosting.Redis", "13.4.0", "nuget-source"))
            {
                addPackageActivity.SetAddPackageSuccess(true);
            }

            addActivity.SetAppHostCandidateCount(1);
            addActivity.SetAppHostLanguage("csharp");
            addActivity.SetAddPackageSearchResultCount(42);
            addActivity.SetAddSelectedPackage("Aspire.Hosting.Redis", "13.4.0", "daily");
        }

        var sessionActivities = GetSessionActivities(startedActivities, "session-1");
        Assert.Collection(
            sessionActivities,
            addActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddCommand, addActivity.OperationName);
                Assert.Equal("redis", addActivity.GetTagItem(ProfilingTelemetry.Tags.AddIntegrationName));
                Assert.Equal(true, addActivity.GetTagItem(ProfilingTelemetry.Tags.AddVersionSpecified));
                Assert.Equal(true, addActivity.GetTagItem(ProfilingTelemetry.Tags.AddSourceSpecified));
                Assert.Equal(true, addActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostProjectFileSpecified));
                Assert.Equal(1, addActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
                Assert.Equal("csharp", addActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostLanguage));
                Assert.Equal(42, addActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageSearchResultCount));
                Assert.Equal("Aspire.Hosting.Redis", addActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageId));
                Assert.Equal("13.4.0", addActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageVersion));
                Assert.Equal("daily", addActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageChannel));
            },
            findAppHostActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddFindAppHost, findAppHostActivity.OperationName);
                Assert.Equal(true, findAppHostActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostProjectFileSpecified));
                Assert.Equal(1, findAppHostActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
            },
            configuredChannelActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddGetConfiguredChannel, configuredChannelActivity.OperationName);
                Assert.Equal("daily", configuredChannelActivity.GetTagItem(ProfilingTelemetry.Tags.AddConfiguredChannel));
            },
            searchPackagesActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddSearchPackages, searchPackagesActivity.OperationName);
                Assert.Equal("daily", searchPackagesActivity.GetTagItem(ProfilingTelemetry.Tags.AddConfiguredChannel));
                Assert.Equal(42, searchPackagesActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageSearchResultCount));
            },
            selectPackageActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddSelectPackage, selectPackageActivity.OperationName);
                Assert.Equal("redis", selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddIntegrationName));
                Assert.Equal(true, selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddVersionSpecified));
                Assert.Equal(1, selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageMatchCount));
                Assert.Equal(ProfilingTelemetry.Values.AddPackageMatchKindExact, selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageMatchKind));
                Assert.Equal("Aspire.Hosting.Redis", selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageId));
                Assert.Equal("13.4.0", selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageVersion));
                Assert.Equal("daily", selectPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageChannel));
            },
            promptActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddSelectPackagePrompt, promptActivity.OperationName);
            },
            stopRunningInstanceActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddStopExistingInstance, stopRunningInstanceActivity.OperationName);
                Assert.Equal("NoInstanceFound", stopRunningInstanceActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostRunningInstanceResult));
            },
            addPackageActivity =>
            {
                Assert.Equal(ProfilingTelemetry.Activities.AddPackage, addPackageActivity.OperationName);
                Assert.Equal("Aspire.Hosting.Redis", addPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageId));
                Assert.Equal("13.4.0", addPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageVersion));
                Assert.Equal(true, addPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddSourceSpecified));
                Assert.Equal(true, addPackageActivity.GetTagItem(ProfilingTelemetry.Tags.AddPackageSuccess));
            });

        Assert.All(sessionActivities, activity =>
        {
            Assert.Equal("session-1", activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
            Assert.Equal("session-1", activity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
        });
    }

    [Fact]
    public void ProfilingSpansReuseSessionFromAmbientActivityBaggage()
    {
        var startedActivities = new ConcurrentQueue<Activity>();
        using var parentSource = new ActivitySource("test-parent");
        using var parentListener = ActivityListenerHelper.Create(parentSource);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Enqueue);
        using var parentActivity = parentSource.StartActivity("parent");
        Assert.NotNull(parentActivity);

        parentActivity.SetBaggage(ProfilingTelemetry.Baggage.SessionId, "session-1");

        using (profilingTelemetry.StartDetachedSpawnChild("aspire", ["run"], childCommand: "start"))
        {
        }

        using (profilingTelemetry.StartDetachedWaitForBackchannel(childProcessId: 1, expectedHash: "hash", hasLegacyHash: false))
        {
        }

        var sessionActivities = GetSessionActivities(startedActivities, "session-1");
        Assert.Equal(2, sessionActivities.Length);
        Assert.All(sessionActivities, activity =>
        {
            Assert.Equal("session-1", activity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
            Assert.Equal("session-1", activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        });
    }

    [Fact]
    public void ProfilingSpansStoreGeneratedSessionOnAmbientAncestorsForSiblings()
    {
        var startedActivities = new ConcurrentQueue<Activity>();
        using var parentSource = new ActivitySource("test-parent");
        using var parentListener = ActivityListenerHelper.Create(parentSource);
        using var diagnosticSource = new ActivitySource("test-diagnostic");
        using var diagnosticListener = ActivityListenerHelper.Create(diagnosticSource);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Enqueue);
        using var parentActivity = parentSource.StartActivity("parent");
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

        var sessionId = parentActivity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId);
        Assert.NotNull(sessionId);
        var sessionActivities = GetSessionActivities(startedActivities, sessionId);
        Assert.Equal(2, sessionActivities.Length);
        Assert.All(sessionActivities, activity =>
        {
            Assert.Equal(sessionId, activity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
            Assert.Equal(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        });
    }

    [Fact]
    public void BackchannelTraceContextCarriesActivityBaggage()
    {
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource);

        using var activity = profilingTelemetry.StartJsonRpcClientCall("aux", "GetCapabilitiesAsync", streaming: false);
        var traceContext = activity.CreateBackchannelTraceContext();

        Assert.NotNull(traceContext);
        Assert.Equal("session-1", traceContext.Baggage[ProfilingTelemetry.Baggage.SessionId]);
    }

    private static Activity[] GetSessionActivities(IEnumerable<Activity> activities, string sessionId)
    {
        return [.. activities.Where(activity => Equals(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId)))];
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
