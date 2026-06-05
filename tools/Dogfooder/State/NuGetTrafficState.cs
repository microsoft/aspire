// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Hex1b.Documents;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.State;

/// <summary>
/// Live snapshot of <see cref="DogfoodingNuGetServer"/> traffic the NuGet
/// analyzer tab renders. Kept hex1b-free (except for the cached read-only
/// editor states the inspector renders) so the (future) self-test command
/// can drive the same state machine headlessly.
/// </summary>
/// <remarks>
/// One instance per session; backfilled with the server's bounded ring
/// buffer when the tab first activates, then live-updated as the server
/// raises <c>RequestCompleted</c>.
/// </remarks>
internal sealed class NuGetTrafficState
{
    private readonly object _gate = new();
    private readonly List<DogfoodingNuGetTrafficEvent> _events = new();
    private const int MaxDisplayedEvents = 200;

    /// <summary>Most recent events first.</summary>
    public IReadOnlyList<DogfoodingNuGetTrafficEvent> Events
    {
        get { lock (_gate) { return _events.ToArray(); } }
    }

    public TrafficFilter Filter { get; set; } = TrafficFilter.All;

    /// <summary>
    /// Id of the event currently shown in the details panel. <c>null</c>
    /// when no row has been selected yet. The view layer falls back to
    /// "first visible row" when this id is null OR no longer present in
    /// the filtered set (e.g. the selected event scrolled off the ring
    /// buffer or the filter changed). Storing the id rather than an index
    /// lets the selection survive new inbound events (which are inserted
    /// at the head, shifting indexes underneath the user).
    /// </summary>
    public Guid? SelectedEventId { get; set; }

    // The Editor widget is stateful (cursor, scroll, selection); rebuilding
    // EditorState on every frame would reset the cursor to (0, 0) and undo
    // any scroll the user did. We cache one upstream + one outgoing editor
    // per selected event id so re-renders preserve scroll/cursor, but switch
    // events evict the previous editor so we don't leak memory across a
    // 200-event ring buffer.
    private Guid? _editorCacheEventId;
    private EditorState? _upstreamEditor;
    private EditorState? _outgoingEditor;

    /// <summary>
    /// Returns a read-only <see cref="EditorState"/> for the upstream
    /// response body of <paramref name="ev"/>, cached by event id so
    /// scroll/cursor survive across re-renders.
    /// </summary>
    public EditorState GetUpstreamEditor(DogfoodingNuGetTrafficEvent ev)
    {
        EnsureCache(ev);
        return _upstreamEditor!;
    }

    /// <summary>
    /// Returns a read-only <see cref="EditorState"/> for the outgoing
    /// (proxy → CLI) response body of <paramref name="ev"/>, cached by
    /// event id so scroll/cursor survive across re-renders.
    /// </summary>
    public EditorState GetOutgoingEditor(DogfoodingNuGetTrafficEvent ev)
    {
        EnsureCache(ev);
        return _outgoingEditor!;
    }

    private void EnsureCache(DogfoodingNuGetTrafficEvent ev)
    {
        if (_editorCacheEventId == ev.Request.Id && _upstreamEditor is not null && _outgoingEditor is not null)
        {
            return;
        }
        _editorCacheEventId = ev.Request.Id;
        _upstreamEditor = MakeReadOnlyEditor(FormatBody(ev.Response.Payloads.UpstreamBody, ev.Response.Payloads.UpstreamBodyTruncated));
        _outgoingEditor = MakeReadOnlyEditor(FormatBody(ev.Response.Payloads.OutgoingBody, ev.Response.Payloads.OutgoingBodyTruncated,
            fallback: $"<binary or non-captured response: {ev.Response.BodyBytes?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "?"} bytes>"));
    }

    private static EditorState MakeReadOnlyEditor(string text)
    {
        var doc = new Hex1bDocument(text);
        // The Editor widget owns its cursor/scroll inside EditorState; we
        // flip IsReadOnly so users can navigate, copy, and search but not
        // mutate the captured payload (it'd silently desync from the
        // event's snapshot).
        return new EditorState(doc) { IsReadOnly = true };
    }

    private static string FormatBody(string? body, bool truncated, string fallback = "<empty body>")
    {
        if (string.IsNullOrEmpty(body))
        {
            return fallback;
        }
        // Pretty-print JSON when the content type advertises it OR the
        // body parses as JSON regardless of header (some NuGet endpoints
        // emit application/json without a charset and others omit the
        // header entirely from the upstream-captured snapshot).
        var pretty = TryPrettyJson(body) ?? body;
        return truncated
            ? pretty + "\n\n… (body was truncated at 32KB capture cap)"
            : pretty;
    }

    private static string? TryPrettyJson(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch
        {
            return null;
        }
    }

    public event Action? OnChanged;

    /// <summary>Notifies listeners (used by the view to invalidate when selection changes).</summary>
    public void NotifyChanged() => OnChanged?.Invoke();

    /// <summary>Replaces all events with <paramref name="backfill"/> ordered oldest→newest.</summary>
    public void Backfill(IReadOnlyList<DogfoodingNuGetTrafficEvent> backfill)
    {
        lock (_gate)
        {
            _events.Clear();
            // Stored newest-first to make the table easier to render.
            _events.AddRange(backfill.OrderByDescending(e => e.Response.CompletedAt));
            Trim();
        }
        OnChanged?.Invoke();
    }

    public void Append(DogfoodingNuGetTrafficEvent ev)
    {
        lock (_gate)
        {
            _events.Insert(0, ev);
            Trim();
        }
        OnChanged?.Invoke();
    }

    private void Trim()
    {
        if (_events.Count > MaxDisplayedEvents)
        {
            _events.RemoveRange(MaxDisplayedEvents, _events.Count - MaxDisplayedEvents);
        }
    }
}

internal enum TrafficFilter
{
    All,
    Search,
    Restore,
    Errors,
}
