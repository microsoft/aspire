// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Aspire.Hosting;
using Aspire.Tests;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryContextTests
{
    [Fact]
    public void AddActivityContextToEnvironment_EmitsActivityValues()
    {
        using var source = new ActivitySource("test-profiling-context");
        using var listener = ActivityListenerHelper.Create(source);
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        activity.SetBaggage(ProfilingTelemetry.Baggage.SessionId, "session-1");
        activity.TraceStateString = "state-1";

        var environment = new Dictionary<string, string>();
        ProfilingTelemetry.AddActivityContextToEnvironment(activity, environment);

        Assert.Equal("true", environment[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        Assert.Equal("session-1", environment[ProfilingTelemetry.EnvironmentVariables.SessionId]);
        Assert.Equal(activity.Id, environment[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
        Assert.Equal("state-1", environment[ProfilingTelemetry.EnvironmentVariables.TraceState]);
        Assert.Equal("true", environment[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal("session-1", environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(activity.Id, environment[KnownConfigNames.Legacy.StartupTraceParent]);
        Assert.Equal("state-1", environment[KnownConfigNames.Legacy.StartupTraceState]);
    }

    [Fact]
    public void AddActivityContextToEnvironment_AllowsMissingActivity()
    {
        var environment = new Dictionary<string, string>();

        ProfilingTelemetry.AddActivityContextToEnvironment(null, environment);

        Assert.Empty(environment);
    }

    [Fact]
    public void AddActivityContextToEnvironment_IgnoresNonProfilingActivity()
    {
        using var source = new ActivitySource("test-profiling-context");
        using var listener = ActivityListenerHelper.Create(source);
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        var environment = new Dictionary<string, string>();
        ProfilingTelemetry.AddActivityContextToEnvironment(activity, environment);

        Assert.Empty(environment);
    }

    [Fact]
    public void StartRunCommand_ContinuesConfiguredRemoteParentAndSession()
    {
        var startedActivities = new List<Activity>();
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (ProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"),
            (ProfilingTelemetry.EnvironmentVariables.TraceParent, "00-0102030405060708090a0b0c0d0e0f10-1112131415161718-01"),
            (ProfilingTelemetry.EnvironmentVariables.TraceState, "state-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Add);

        using var activity = profilingTelemetry.StartRunCommand();

        Assert.True(activity.IsRunning);
        var startedActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.RunCommand);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", startedActivity.TraceId.ToString());
        Assert.Equal("session-1", startedActivity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    public void IsEnabled_ReturnsExpectedValue(string? enabled, bool expected)
    {
        var isEnabled = ProfilingTelemetry.IsProfilingEnabled(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, enabled)));

        Assert.Equal(expected, isEnabled);
    }

    [Fact]
    public void StartRunCommand_ReadsLegacyStartupNames()
    {
        var startedActivities = new List<Activity>();
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (KnownConfigNames.Legacy.StartupProfilingEnabled, "true"),
            (KnownConfigNames.Legacy.StartupOperationId, "session-1"),
            (KnownConfigNames.Legacy.StartupTraceParent, "00-0102030405060708090a0b0c0d0e0f10-1112131415161718-01"),
            (KnownConfigNames.Legacy.StartupTraceState, "state-1")));
        using var listener = ActivityListenerHelper.Create(profilingTelemetry.ActivitySource, onActivityStarted: startedActivities.Add);

        using var activity = profilingTelemetry.StartRunCommand();

        Assert.True(activity.IsRunning);
        var startedActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.RunCommand);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", startedActivity.TraceId.ToString());
        Assert.Equal("session-1", startedActivity.GetBaggageItem(ProfilingTelemetry.Baggage.SessionId));
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
