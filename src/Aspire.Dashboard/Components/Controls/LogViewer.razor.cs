// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

/// <summary>
/// A log viewing UI component that shows a live view of a log, with syntax highlighting and automatic scrolling.
/// </summary>
public sealed partial class LogViewer
{
    private static readonly TimeSpan s_emptyLogsMessageDelay = TimeSpan.FromSeconds(2);
    private static readonly MarkupString s_spaceMarkup = new MarkupString("&#32;");

    private LogEntries? _logEntries;
    private bool _logsChanged;
    private bool _showEmptyLogsMessage;
    private CancellationTokenSource? _emptyLogsMessageDelayCts;

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required DimensionManager DimensionManager { get; init; }

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

            // Set max item count when the Virtualize component is set.
            if (field != null)
            {
                VirtualizeHelper<LogEntry>.TrySetMaxItemCount(field, 10_000);
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
        UpdateEmptyLogsMessageState();
        StateHasChanged();
    }

    protected override void OnParametersSet()
    {
        if (_logEntries != LogEntries)
        {
            Logger.LogDebug("Log entries changed.");

            _logsChanged = true;
            _logEntries = LogEntries;
            ResetEmptyLogsMessage();
        }

        UpdateEmptyLogsMessageState();
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

    private bool IsLogEntriesEmpty() => _logEntries?.EntriesCount == 0;

    private void UpdateEmptyLogsMessageState()
    {
        if (IsLogEntriesEmpty())
        {
            StartEmptyLogsMessageDelay();
        }
        else
        {
            ResetEmptyLogsMessage();
        }
    }

    private void StartEmptyLogsMessageDelay()
    {
        if (_showEmptyLogsMessage || _emptyLogsMessageDelayCts is not null)
        {
            return;
        }

        _emptyLogsMessageDelayCts = new CancellationTokenSource();
        _ = ShowEmptyLogsMessageAfterDelayAsync(_emptyLogsMessageDelayCts);
    }

    private async Task ShowEmptyLogsMessageAfterDelayAsync(CancellationTokenSource delayCts)
    {
        try
        {
            await Task.Delay(s_emptyLogsMessageDelay, delayCts.Token);

            await InvokeAsync(() =>
            {
                if (_emptyLogsMessageDelayCts == delayCts && IsLogEntriesEmpty())
                {
                    _showEmptyLogsMessage = true;
                    StateHasChanged();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_emptyLogsMessageDelayCts == delayCts)
            {
                _emptyLogsMessageDelayCts = null;
            }

            delayCts.Dispose();
        }
    }

    private void ResetEmptyLogsMessage()
    {
        _showEmptyLogsMessage = false;
        _emptyLogsMessageDelayCts?.Cancel();
        _emptyLogsMessageDelayCts = null;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_logsChanged)
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            _logsChanged = false;
        }
        if (firstRender)
        {
            Logger.LogDebug("Initializing log viewer.");

            await JS.InvokeVoidAsync("initializeContinuousScroll");
            DimensionManager.OnViewportInformationChanged += OnBrowserResize;
        }
    }

    private void OnBrowserResize(object? o, EventArgs args)
    {
        InvokeAsync(async () =>
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            await JS.InvokeVoidAsync("initializeContinuousScroll");
        });
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

        ResetEmptyLogsMessage();
        DimensionManager.OnViewportInformationChanged -= OnBrowserResize;
        return ValueTask.CompletedTask;
    }
}
