// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Dashboard.Components.Pages.StructuredLogs;

namespace Aspire.Dashboard.Tests.Model;

public sealed class StructuredLogsPageViewModelTests
{
    [Fact]
    public void NoSelectedEntry_ReturnsNotExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = null;

        var result = vm.IsSelectedLogEntryExcludedByFilters("", []);

        Assert.False(result);
    }

    [Fact]
    public void EntryMatchesAllFilters_ReturnsNotExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Information);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Warning, "Hello world");

        var result = vm.IsSelectedLogEntryExcludedByFilters("Hello", []);

        Assert.False(result);
    }

    [Fact]
    public void EntryBelowLogLevel_ReturnsExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello world");

        var result = vm.IsSelectedLogEntryExcludedByFilters("", []);

        Assert.True(result);
    }

    [Fact]
    public void EntryAtExactLogLevel_ReturnsNotExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Warning, "Hello world");

        var result = vm.IsSelectedLogEntryExcludedByFilters("", []);

        Assert.False(result);
    }

    [Fact]
    public void LogLevelFilterIsAll_ReturnsNotExcluded()
    {
        // LogLevel null means "All" is selected
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Trace, "any message");

        var result = vm.IsSelectedLogEntryExcludedByFilters("", []);

        Assert.False(result);
    }

    [Fact]
    public void TextFilterDoesNotMatch_ReturnsExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello world");

        var result = vm.IsSelectedLogEntryExcludedByFilters("xyz-not-present", []);

        Assert.True(result);
    }

    [Fact]
    public void TextFilterMatchesCaseInsensitive_ReturnsNotExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello World");

        var result = vm.IsSelectedLogEntryExcludedByFilters("hello", []);

        Assert.False(result);
    }

    [Fact]
    public void TextFilterMatchesResourceName_ReturnsExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "some message");

        // The text filter only checks the Message field (matching StructuredLogsViewModel.GetFilters() behavior).
        // A resource name match is not sufficient to keep the entry visible.
        var result = vm.IsSelectedLogEntryExcludedByFilters("app1", []);

        Assert.True(result);
    }

    [Fact]
    public void TextFilterMatchesSeverity_ReturnsExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "some message");

        // The text filter only checks the Message field (matching StructuredLogsViewModel.GetFilters() behavior).
        // Matching the severity text is not sufficient to keep the entry visible.
        var result = vm.IsSelectedLogEntryExcludedByFilters("Information", []);

        Assert.True(result);
    }

    [Fact]
    public void FieldFilterExcludesEntry_ReturnsExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz-not-present",
            Enabled = true
        };

        var result = vm.IsSelectedLogEntryExcludedByFilters("", [fieldFilter]);

        Assert.True(result);
    }

    [Fact]
    public void FieldFilterMatchesEntry_ReturnsNotExcluded()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "Hello",
            Enabled = true
        };

        var result = vm.IsSelectedLogEntryExcludedByFilters("", [fieldFilter]);

        Assert.False(result);
    }

    [Fact]
    public void DisabledFieldFilter_IsIgnored()
    {
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz-not-present",
            Enabled = false
        };

        var result = vm.IsSelectedLogEntryExcludedByFilters("", [fieldFilter]);

        Assert.False(result);
    }

    [Fact]
    public void MultipleFiltersAllMustPass()
    {
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = CreateLogDetailsViewModel(LogLevel.Warning, "Hello world");

        // Text filter matches, field filter does NOT match
        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz",
            Enabled = true
        };

        var result = vm.IsSelectedLogEntryExcludedByFilters("Hello", [fieldFilter]);

        Assert.True(result);
    }

    private static StructuredLogsPageViewModel CreateViewModel(LogLevel? selectedLogLevel)
    {
        return new StructuredLogsPageViewModel
        {
            SelectedResource = new SelectViewModel<ResourceTypeDetails> { Name = "All", Id = null },
            SelectedLogLevel = new SelectViewModel<LogLevel?> { Name = selectedLogLevel?.ToString() ?? "All", Id = selectedLogLevel }
        };
    }

    private static StructureLogsDetailsViewModel CreateLogDetailsViewModel(LogLevel severity, string message)
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var resource = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var resourceView = new OtlpResourceView(resource, new RepeatedField<KeyValue>());
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var severityNumber = severity switch
        {
            LogLevel.Trace => SeverityNumber.Trace,
            LogLevel.Debug => SeverityNumber.Debug,
            LogLevel.Information => SeverityNumber.Info,
            LogLevel.Warning => SeverityNumber.Warn,
            LogLevel.Error => SeverityNumber.Error,
            LogLevel.Critical => SeverityNumber.Fatal,
            _ => SeverityNumber.Unspecified
        };

        var logRecord = TelemetryTestHelpers.CreateLogRecord(message: message, severity: severityNumber);
        var logEntry = new OtlpLogEntry(logRecord, resourceView, scope, context);

        return new StructureLogsDetailsViewModel { LogEntry = logEntry };
    }
}
