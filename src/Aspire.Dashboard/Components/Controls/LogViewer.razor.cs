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
    private const string ScrollContainerId = "logScrollContainer";
    private static readonly MarkupString s_spaceMarkup = new MarkupString("&#32;");

    private LogEntries? _logEntries;
    private bool _logsChanged;

    private IList<LogEntry>? _visibleEntriesCache;
    private string? _appliedFilterText;
    private bool _filterChanged;

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

    [Parameter]
    public bool ShowNoLogsMessage { get; set; }

    [Parameter]
    public string? FilterText { get; set; }

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

        // Entries may have been appended or evicted (circular buffer) since the last render, so drop
        // the cached filtered view before Virtualize re-queries through GetItems.
        _visibleEntriesCache = null;

        await VirtualizeRef.RefreshDataAsync();
        StateHasChanged();
    }

    protected override void OnParametersSet()
    {
        if (_logEntries != LogEntries)
        {
            Logger.LogDebug("Log entries changed.");

            _logsChanged = true;
            _logEntries = LogEntries;
            _visibleEntriesCache = null;
        }

        if (!string.Equals(_appliedFilterText, FilterText, StringComparison.Ordinal))
        {
            _appliedFilterText = FilterText;
            _visibleEntriesCache = null;

            // Virtualize caches the items it last fetched and only re-queries GetItems on an explicit
            // RefreshDataAsync. The filter is applied inside GetItems, so the refresh must run after
            // FilterText has been assigned here (in OnAfterRenderAsync) - refreshing earlier, e.g. from
            // the parent's bind handler, would re-query with the previous filter value.
            _filterChanged = true;
        }

        base.OnParametersSet();
    }

    private ValueTask<ItemsProviderResult<LogEntry>> GetItems(ItemsProviderRequest r)
    {
        var entries = GetVisibleEntries();
        return ValueTask.FromResult(new ItemsProviderResult<LogEntry>(entries.Skip(r.StartIndex).Take(r.Count), entries.Count));
    }

    private IList<LogEntry> GetVisibleEntries()
    {
        if (_visibleEntriesCache is { } cached)
        {
            return cached;
        }

        var entries = _logEntries?.GetEntries();
        if (entries is null)
        {
            return _visibleEntriesCache = Array.Empty<LogEntry>();
        }

        var filterText = FilterText;
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return _visibleEntriesCache = entries;
        }

        return _visibleEntriesCache = entries.Where(e => MatchesFilter(e, filterText)).ToList();
    }

    // Filter on the ANSI-stripped raw content, which is the plain text the user actually sees
    // (including the timestamp). Content can't be used because it contains embedded HTML links and
    // other markup added during ANSI conversion, and the unstripped RawContent still contains raw
    // ANSI escape sequences - matching against either produces false negatives when the search term
    // spans markup or a color boundary. Pause markers aren't log content, so they're hidden while a
    // filter is active.
    private static bool MatchesFilter(LogEntry entry, string filterText)
        => entry.Type != LogEntryType.Pause
            && entry.GetStrippedRawContent() is { } stripped
            && stripped.Contains(filterText, StringComparison.OrdinalIgnoreCase);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_logsChanged)
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            _logsChanged = false;
        }
        if (_filterChanged)
        {
            _filterChanged = false;
            await RefreshDataAsync();
        }
        if (firstRender)
        {
            Logger.LogDebug("Initializing log viewer.");

            await JS.InvokeVoidAsync("initializeContinuousScroll");
            // Focus the scroll container without showing the focus ring. The container is a large
            // content area where a visible focus indicator would be visually noisy on initial load.
            await JS.InvokeVoidAsync("focusElement", ScrollContainerId, true);
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

        DimensionManager.OnViewportInformationChanged -= OnBrowserResize;
        return ValueTask.CompletedTask;
    }
}
