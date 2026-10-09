// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Telemetry;

namespace Aspire.Dashboard.Tests;

public class TestDashboardTelemetrySender : IDashboardTelemetrySender
{
    public bool IsTelemetryEnabled { get; init; }
    public Channel<OperationContext> ContextChannel { get; } = Channel.CreateUnbounded<OperationContext>();
    public Channel<Func<HttpClient, Func<OperationContextProperty, object>, Task>> RequestChannel { get; } = Channel.CreateUnbounded<Func<HttpClient, Func<OperationContextProperty, object>, Task>>();
    public TelemetrySessionState State { get; private set; }

    public Task<bool> TryStartTelemetrySessionAsync()
    {
        State = IsTelemetryEnabled ? TelemetrySessionState.Enabled : TelemetrySessionState.Disabled;
        return Task.FromResult(IsTelemetryEnabled);
    }

    public void QueueRequest(OperationContext context, Func<HttpClient, Func<OperationContextProperty, object>, Task> requestFunc)
    {
        ContextChannel.Writer.TryWrite(context);
        RequestChannel.Writer.TryWrite(requestFunc);
    }

    public ValueTask DisposeAsync()
    {
        ContextChannel.Writer.Complete();
        RequestChannel.Writer.Complete();
        return ValueTask.CompletedTask;
    }
}
