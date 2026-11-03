// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class TelemetryHelpSetupHelpers
{
    public static TestDialogService SetupDialogs(BunitContext context, OtlpEndpointInfo? endpoints = null, Action<DashboardOptions>? configure = null)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(context);
        FluentUISetupHelpers.SetupFluentTextField(context);
        FluentUISetupHelpers.SetupFluentButton(context);
        context.JSInterop.SetupModule("/Components/Controls/MarkdownRenderer.razor.js")
            .SetupVoid("highlightCodeBlocks", _ => true);
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        context.Services.AddOptions<DashboardOptions>().Configure(options => configure?.Invoke(options));
        context.Services.AddSingleton(endpoints ?? new OtlpEndpointInfo(() => null, () => null));
        context.Services.AddSingleton<TelemetryImportService>();
        var dialogService = new TestDialogService();
        context.Services.AddSingleton<IDialogService>(dialogService);

        return dialogService;
    }

    public static Task AddSignalAsync(ITelemetryRepositoryWriter repository, AspireDataType signal) => signal switch
    {
        AspireDataType.StructuredLogs => repository.AddLogsAsync(new(),
        [
            new ResourceLogs
            {
                Resource = CreateResource("test"),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: "test") } } }
            }
        ]),
        AspireDataType.Traces => repository.AddTracesAsync(new(),
        [
            new ResourceSpans
            {
                Resource = CreateResource("test"),
                ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans = { CreateSpan("trace", "span", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1)) } } }
            }
        ]),
        AspireDataType.Metrics => repository.AddMetricsAsync(new(),
        [
            new ResourceMetrics
            {
                Resource = CreateResource("test"),
                ScopeMetrics = { new ScopeMetrics { Scope = CreateScope(), Metrics = { CreateSumMetric("test", DateTime.UnixEpoch) } } }
            }
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };
}
