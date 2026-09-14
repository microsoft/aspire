// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;
using Xunit;
using static Aspire.Dashboard.Components.Pages.TraceDetail;

namespace Aspire.Dashboard.Tests.Model;

public sealed class TraceDetailPageViewModelTests
{
    [Fact]
    public void NoSelectedData_ReturnsNotExcluded()
    {
        var vm = new TraceDetailPageViewModel
        {
            SelectedData = null,
            SelectedSpanType = new SelectViewModel<SpanType> { Name = "All", Id = default }
        };

        var result = vm.IsSelectedDataExcludedByFilters([]);

        Assert.False(result);
    }

    [Fact]
    public void SelectedSpanPresentInVisible_ReturnsNotExcluded()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var span = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "span1", parentSpanId: null, startDate: DateTime.UtcNow);

        var spanVm = CreateSpanWaterfallViewModel(span);

        var vm = new TraceDetailPageViewModel
        {
            SelectedData = new TraceDetailSelectedDataViewModel
            {
                SpanViewModel = new SpanDetailsViewModel
                {
                    Span = span,
                    Properties = [],
                    Links = [],
                    Backlinks = [],
                    Title = "Test",
                    Resources = []
                }
            },
            SelectedSpanType = new SelectViewModel<SpanType> { Name = "All", Id = default }
        };

        var result = vm.IsSelectedDataExcludedByFilters([spanVm]);

        Assert.False(result);
    }

    [Fact]
    public void SelectedSpanNotInVisible_ReturnsExcluded()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var selectedSpan = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "span1", parentSpanId: null, startDate: DateTime.UtcNow);
        var otherSpan = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "span2", parentSpanId: null, startDate: DateTime.UtcNow);

        var otherVm = CreateSpanWaterfallViewModel(otherSpan);

        var vm = new TraceDetailPageViewModel
        {
            SelectedData = new TraceDetailSelectedDataViewModel
            {
                SpanViewModel = new SpanDetailsViewModel
                {
                    Span = selectedSpan,
                    Properties = [],
                    Links = [],
                    Backlinks = [],
                    Title = "Test",
                    Resources = []
                }
            },
            SelectedSpanType = new SelectViewModel<SpanType> { Name = "All", Id = default }
        };

        var result = vm.IsSelectedDataExcludedByFilters([otherVm]);

        Assert.True(result);
    }

    [Fact]
    public void SelectedLogPresentInSpanLogs_ReturnsNotExcluded()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var resourceView = new OtlpResourceView(resource, new RepeatedField<KeyValue>());
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var span = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "span1", parentSpanId: null, startDate: DateTime.UtcNow);
        var logEntry = new OtlpLogEntry(TelemetryTestHelpers.CreateLogRecord(message: "test log"), resourceView, scope, context);

        var spanVm = CreateSpanWaterfallViewModel(span, [new SpanLogEntryViewModel { Index = 0, LogEntry = logEntry, LeftOffset = 0 }]);

        var vm = new TraceDetailPageViewModel
        {
            SelectedData = new TraceDetailSelectedDataViewModel
            {
                LogEntryViewModel = new StructureLogsDetailsViewModel { LogEntry = logEntry }
            },
            SelectedSpanType = new SelectViewModel<SpanType> { Name = "All", Id = default }
        };

        var result = vm.IsSelectedDataExcludedByFilters([spanVm]);

        Assert.False(result);
    }

    [Fact]
    public void SelectedLogNotInAnySpanLogs_ReturnsExcluded()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var resourceView = new OtlpResourceView(resource, new RepeatedField<KeyValue>());
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var span = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "span1", parentSpanId: null, startDate: DateTime.UtcNow);
        var logEntry = new OtlpLogEntry(TelemetryTestHelpers.CreateLogRecord(message: "test log"), resourceView, scope, context);

        // Span with no logs
        var spanVm = CreateSpanWaterfallViewModel(span);

        var vm = new TraceDetailPageViewModel
        {
            SelectedData = new TraceDetailSelectedDataViewModel
            {
                LogEntryViewModel = new StructureLogsDetailsViewModel { LogEntry = logEntry }
            },
            SelectedSpanType = new SelectViewModel<SpanType> { Name = "All", Id = default }
        };

        var result = vm.IsSelectedDataExcludedByFilters([spanVm]);

        Assert.True(result);
    }

    private static SpanWaterfallViewModel CreateSpanWaterfallViewModel(OtlpSpan span, List<SpanLogEntryViewModel>? spanLogs = null)
    {
        return new SpanWaterfallViewModel
        {
            Span = span,
            Children = [],
            LeftOffset = 0,
            Width = 100,
            Depth = 0,
            LabelIsRight = true,
            UninstrumentedPeer = null,
            SpanLogs = spanLogs ?? [],
            IsHidden = false
        };
    }
}
