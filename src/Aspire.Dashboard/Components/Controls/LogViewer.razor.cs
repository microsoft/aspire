// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace Aspire.Dashboard.Components;

/// <summary>
/// A log viewing UI component that shows a live view of a log, with syntax highlighting and automatic scrolling.
/// </summary>
public sealed partial class LogViewer
{
    private static readonly MarkupString s_spaceMarkup = new MarkupString("&#32;");

    private LogEntries? _logEntries;

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILogger<LogViewer> Logger { get; init; }

    [Parameter]
    public LogEntries? LogEntries { get; set; } = null!;

    [Parameter]
    public bool ShowTimestamp { get; set; }

    [Parameter]
    public bool ShowResourcePrefix { get; set; }

    [Parameter]
    public bool IsTimestampUtc { get; set; }

    [Parameter]
    public bool NoWrapLogs { get; set; }

    private Virtualize<LogEntry>? VirtualizeRef
    {
        get => field;
        set
        {
            field = value;

            // Set max item count and anchor mode when the Virtualize component is set.
            if (field != null)
            {
                VirtualizeHelper<LogEntry>.TrySetMaxItemCount(field, 10_000);
                VirtualizeHelper<LogEntry>.TrySetAnchorModeEnd(field);
            }
        }
    }

    public async Task RefreshDataAsync()
    {
        if (VirtualizeRef == null)
        {
            return;
        }

        await VirtualizeRef.RefreshDataAsync();
        StateHasChanged();
    }

    protected override void OnParametersSet()
    {
        if (_logEntries != LogEntries)
        {
            Logger.LogDebug("Log entries changed.");

            _logEntries = LogEntries;
        }

        base.OnParametersSet();
    }

    private ValueTask<ItemsProviderResult<LogEntry>> GetItems(ItemsProviderRequest r)
    {
        var entries = _logEntries?.GetEntries();
        if (entries == null)
        {
            return ValueTask.FromResult(new ItemsProviderResult<LogEntry>(Enumerable.Empty<LogEntry>(), 0));
        }

        return ValueTask.FromResult(new ItemsProviderResult<LogEntry>(entries.Skip(r.StartIndex).Take(r.Count), entries.Count));
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Logger.LogDebug("Initializing log viewer.");
        }
    }

    private string GetDisplayTimestamp(DateTimeOffset timestamp)
    {
        return IsTimestampUtc
            ? timestamp.UtcDateTime.ToString(KnownFormats.ConsoleLogsUITimestampUtcFormat, CultureInfo.InvariantCulture)
            : TimeProvider.ToLocal(timestamp).ToString(KnownFormats.ConsoleLogsUITimestampLocalFormat, CultureInfo.InvariantCulture);
    }

    private string GetLogContainerClass()
    {
        return $"log-container console-container {(NoWrapLogs ? "wrap-log-container" : null)}";
    }

    public ValueTask DisposeAsync()
    {
        Logger.LogDebug("Disposing log viewer.");

        return ValueTask.CompletedTask;
    }
}
