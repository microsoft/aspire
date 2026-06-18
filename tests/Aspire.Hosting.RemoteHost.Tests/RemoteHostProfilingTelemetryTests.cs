// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Tests;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class RemoteHostProfilingTelemetryTests
{
    [Fact]
    public void StartRemoteHostRun_RestoresConfiguredParentAndSession()
    {
        using var parentSource = new ActivitySource("test-remotehost-parent");
        using var parentListener = ActivityListenerHelper.Create(parentSource, onActivityStopped: _ => { });
        ActivityTraceId parentTraceId;
        ActivitySpanId parentSpanId;
        string traceParent;
        string? traceState;

        using (var parent = parentSource.StartActivity("parent"))
        {
            Assert.NotNull(parent);
            parentTraceId = parent.TraceId;
            parentSpanId = parent.SpanId;
            traceParent = parent.Id!;
            traceState = parent.TraceStateString;
        }

        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (RemoteHostProfilingTelemetry.EnvironmentVariables.SessionId, "session-1"),
            (RemoteHostProfilingTelemetry.EnvironmentVariables.TraceParent, traceParent),
            (RemoteHostProfilingTelemetry.EnvironmentVariables.TraceState, traceState)));
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetry.ActivitySource, onActivityStopped: activities.Add);

        using (telemetry.StartRemoteHostRun())
        {
        }

        var activity = Assert.Single(activities);
        Assert.Equal(RemoteHostProfilingTelemetry.Activities.RemoteHostRun, activity.OperationName);
        Assert.Equal(parentTraceId, activity.TraceId);
        Assert.Equal(parentSpanId, activity.ParentSpanId);
        Assert.Equal("session-1", activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.ProfilingSessionId));
        Assert.Equal("session-1", activity.GetBaggageItem(RemoteHostProfilingTelemetry.Tags.ProfilingSessionId));
    }

    [Fact]
    public void StartRemoteHostRun_DoesNotStartWhenProfilingIsDisabled()
    {
        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration());
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetry.ActivitySource, onActivityStopped: activities.Add);

        using (telemetry.StartRemoteHostRun())
        {
        }

        Assert.Empty(activities);
    }

    [Fact]
    public void CapabilityScan_AddsLowCardinalityCounts()
    {
        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetry.ActivitySource, onActivityStopped: activities.Add);

        using (var scope = telemetry.StartCapabilityScan(assemblyCount: 3, firstScan: true))
        {
            scope.SetAtsCounts(
                capabilityCount: 4,
                handleTypeCount: 5,
                dtoTypeCount: 6,
                enumTypeCount: 7,
                exportedValueCount: 8,
                diagnosticCount: 9);
        }

        var activity = Assert.Single(activities);
        Assert.Equal(RemoteHostProfilingTelemetry.Activities.CapabilityScan, activity.OperationName);
        Assert.Equal(3, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.AssemblyCount));
        Assert.Equal(true, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityScanFirstScan));
        Assert.Equal(4, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityCount));
        Assert.Equal(9, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.DiagnosticCount));
    }

    [Fact]
    public void AssemblyLoad_AddsAssemblyNames()
    {
        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetry.ActivitySource, onActivityStopped: activities.Add);

        using (var scope = telemetry.StartAssemblyLoad(cacheHit: false))
        {
            scope.SetAssemblyRequestedNames(["Aspire.Hosting.Redis", "Aspire.Hosting", "Aspire.Hosting.Redis"]);
            scope.SetAssemblyLoadedNames([typeof(RemoteHostProfilingTelemetry).Assembly]);
        }

        var activity = Assert.Single(activities);
        Assert.Equal(RemoteHostProfilingTelemetry.Activities.AssemblyLoad, activity.OperationName);
        Assert.Equal(false, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.AssemblyCacheHit));
        var requestedNames = Assert.IsType<string[]>(activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.AssemblyRequestedNames));
        Assert.Equal(["Aspire.Hosting", "Aspire.Hosting.Redis"], requestedNames);
        Assert.Contains("Aspire.Hosting.RemoteHost", Assert.IsType<string[]>(activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.AssemblyLoadedNames)));
    }

    [Fact]
    public void JsonRpcInvokeCapability_AddsCapabilityAndArgumentShape()
    {
        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true")));
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(telemetry.ActivitySource, onActivityStopped: activities.Add);

        using (telemetry.StartJsonRpcInvokeCapability(
            "aspire.redis/addRedis@1",
            new JsonObject
            {
                ["context"] = "ctx-1",
                ["name"] = "redis"
            }))
        {
        }

        var activity = Assert.Single(activities);
        Assert.Equal(RemoteHostProfilingTelemetry.Activities.JsonRpcServerCall, activity.OperationName);
        Assert.Equal("invokeCapability", activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.JsonRpcMethod));
        Assert.Equal("aspire.redis/addRedis@1", activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityId));
        Assert.Equal("aspire.redis", activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityPackage));
        Assert.Equal(2, activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityArgumentCount));
        var argumentNames = Assert.IsType<string[]>(activity.GetTagItem(RemoteHostProfilingTelemetry.Tags.CapabilityArgumentNames));
        Assert.Equal(["context", "name"], argumentNames);
    }

    [Fact]
    public void JsonRpcServerCall_UsesJsonRpcRemoteParent()
    {
        var telemetry = new RemoteHostProfilingTelemetry(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            (RemoteHostProfilingTelemetry.EnvironmentVariables.SessionId, "session-1")));
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.ActivitySource) || source.Name is "test.client" or "test.jsonrpc",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (ReferenceEquals(activity.Source, telemetry.ActivitySource))
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var clientSource = new ActivitySource("test.client");
        using var jsonRpcSource = new ActivitySource("test.jsonrpc");
        var clientActivity = clientSource.StartActivity("client", ActivityKind.Client);
        Assert.NotNull(clientActivity);
        var clientContext = clientActivity.Context;
        var clientSpanId = clientActivity.SpanId;
        clientActivity.Dispose();

        using (var jsonRpcActivity = jsonRpcSource.StartActivity("server", ActivityKind.Server, clientContext))
        {
            Assert.NotNull(jsonRpcActivity);
            using var activity = telemetry.StartJsonRpcServerCall("authenticate", streaming: false);
        }

        var serverActivity = Assert.Single(activities, activity => activity.OperationName == RemoteHostProfilingTelemetry.Activities.JsonRpcServerCall);
        Assert.Equal(clientSpanId, serverActivity.ParentSpanId);
    }

    [Fact]
    public void ShouldConfigureExporter_RequiresProfilingAndOtlpEndpoint()
    {
        Assert.False(RemoteHostProfilingTelemetry.ShouldConfigureExporter(CreateConfiguration(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:18889"))));

        Assert.True(RemoteHostProfilingTelemetry.ShouldConfigureExporter(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:18889"))));

        Assert.False(RemoteHostProfilingTelemetry.ShouldConfigureExporter(CreateConfiguration(
            (RemoteHostProfilingTelemetry.EnvironmentVariables.Enabled, "true"),
            ("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "http://localhost:18889"))));
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
