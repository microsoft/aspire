// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Dashboard.Components.Pages.StructuredLogs;

namespace Aspire.Dashboard.Tests.Model;

public sealed class StructuredLogsPageViewModelTests
{
    [Fact]
    public async Task NoSelectedEntry_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = null;

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task EntryMatchesAllFilters_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Information);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Warning, "Hello world");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "Hello", [], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task EntryBelowLogLevel_ReturnsExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello world");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [], CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task EntryAtExactLogLevel_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Warning, "Hello world");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task LogLevelFilterIsAll_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        // LogLevel null means "All" is selected
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Trace, "any message");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TextFilterDoesNotMatch_ReturnsExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello world");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "xyz-not-present", [], CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TextFilterMatchesCaseInsensitive_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello World");

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "hello", [], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TextFilterMatchesResourceName_ReturnsExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "some message");

        // The text filter only checks the Message field (matching StructuredLogsViewModel.GetFilters() behavior).
        // A resource name match is not sufficient to keep the entry visible.
        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "app1", [], CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TextFilterMatchesSeverity_ReturnsExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "some message");

        // The text filter only checks the Message field (matching StructuredLogsViewModel.GetFilters() behavior).
        // Matching the severity text is not sufficient to keep the entry visible.
        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "Information", [], CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task FieldFilterExcludesEntry_ReturnsExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz-not-present",
            Enabled = true
        };

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [fieldFilter], CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task FieldFilterMatchesEntry_ReturnsNotExcluded()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "Hello",
            Enabled = true
        };

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [fieldFilter], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task DisabledFieldFilter_IsIgnored()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: null);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Information, "Hello world");

        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz-not-present",
            Enabled = false
        };

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "", [fieldFilter], CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task MultipleFiltersAllMustPass()
    {
        using var repositoryContext = SqliteRepositoryTestHelpers.CreateTemporaryTelemetryRepository();
        var vm = CreateViewModel(selectedLogLevel: LogLevel.Warning);
        vm.SelectedLogEntry = await CreateLogDetailsViewModelAsync(repositoryContext.Repository, LogLevel.Warning, "Hello world");

        // Text filter matches, field filter does NOT match
        var fieldFilter = new FieldTelemetryFilter
        {
            Field = nameof(OtlpLogEntry.Message),
            Condition = FilterCondition.Contains,
            Value = "xyz",
            Enabled = true
        };

        var result = await vm.IsSelectedLogEntryExcludedByFiltersAsync(repositoryContext.Repository, "Hello", [fieldFilter], CancellationToken.None);

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

    private static async Task<StructureLogsDetailsViewModel> CreateLogDetailsViewModelAsync(SqliteTelemetryRepository repository, LogLevel severity, string message)
    {
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

        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = TelemetryTestHelpers.CreateResource("app1", "instance"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        LogRecords = { TelemetryTestHelpers.CreateLogRecord(message: message, severity: severityNumber) }
                    }
                }
            }
        });
        var logs = await repository.GetLogsAsync(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 1,
            Filters = []
        }, CancellationToken.None);
        return new StructureLogsDetailsViewModel { LogEntry = Assert.Single(logs.Items) };
    }
}
