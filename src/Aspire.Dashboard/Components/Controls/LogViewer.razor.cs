// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
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
    private bool _appliedShowTimestamp;
    private bool _appliedShowResourcePrefix;
    private bool _appliedIsTimestampUtc;
    private bool _visibleEntriesChanged;

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
        // Entries may have been appended or evicted (circular buffer) since the last render, so drop
        // the cached filtered view before Virtualize re-queries through GetItems.
        _visibleEntriesCache = null;

        await RefreshVirtualizeAsync();
    }

    private async Task RefreshVirtualizeAsync()
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

            _logsChanged = true;
            _logEntries = LogEntries;
            _visibleEntriesCache = null;
        }

        var filterChanged = !string.Equals(_appliedFilterText, FilterText, StringComparison.Ordinal);
        var searchableFieldsChanged =
            _appliedShowTimestamp != ShowTimestamp ||
            _appliedShowResourcePrefix != ShowResourcePrefix ||
            _appliedIsTimestampUtc != IsTimestampUtc;

        _appliedFilterText = FilterText;
        _appliedShowTimestamp = ShowTimestamp;
        _appliedShowResourcePrefix = ShowResourcePrefix;
        _appliedIsTimestampUtc = IsTimestampUtc;

        if (filterChanged || (searchableFieldsChanged && !string.IsNullOrWhiteSpace(FilterText)))
        {
            _visibleEntriesCache = null;

            // Virtualize only re-queries GetItems on an explicit RefreshDataAsync call.
            // We can't call it here (OnParametersSet) because Virtualize.RefreshDataAsync()
            // triggers a child-component re-render mid-lifecycle, which creates re-entrant
            // rendering in Blazor Server. Additionally, OnParametersSetAsync would cause a
            // double-render (sync portion renders stale items, then re-renders after await).
            // Deferring to OnAfterRenderAsync guarantees the full component tree has rendered
            // with the new state, and the cache is already warm from the razor markup's call
            // to GetVisibleEntries() (for the "no logs match" message).
            _visibleEntriesChanged = true;
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

    private bool MatchesFilter(LogEntry entry, string filterText)
    {
        if (entry.Type is LogEntryType.Pause)
        {
            return false;
        }

        return GetSearchableText(entry).Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    private string GetSearchableText(LogEntry entry)
    {
        var builder = new StringBuilder();

        // Keep this in sync with the row markup in LogViewer.razor. Filtering should match only text
        // users can see: optional/display-formatted timestamps, optional resource prefixes, the stderr
        // badge, and the ANSI-stripped log message. RawContent is not enough because it contains hidden
        // ISO timestamps and raw ANSI escape sequences.
        if (ShowTimestamp && entry.Timestamp is { } timestamp)
        {
            AppendSearchablePart(builder, GetDisplayTimestamp(timestamp));
        }

        if (ShowResourcePrefix && entry.ResourcePrefix is { } resourcePrefix)
        {
            AppendSearchablePart(builder, resourcePrefix);
        }

        if (entry.Type is LogEntryType.Error)
        {
            AppendSearchablePart(builder, "stderr");
        }

        if (entry.GetStrippedLogContent() is { } content)
        {
            AppendSearchablePart(builder, content);
        }

        return builder.ToString();
    }

    private static void AppendSearchablePart(StringBuilder builder, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(value);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_logsChanged)
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            _logsChanged = false;
        }
        if (_visibleEntriesChanged)
        {
            _visibleEntriesChanged = false;

            // The filtered view was already rebuilt for the new parameters during this render pass
            // (GetVisibleEntries runs from the markup to decide the "no logs match" message), so just
            // re-query Virtualize. Calling the public RefreshDataAsync here would null the cache and
            // force a second full scan of the log buffer.
            await RefreshVirtualizeAsync();
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
