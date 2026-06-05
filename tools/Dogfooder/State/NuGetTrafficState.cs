// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;

namespace Aspire.Dogfooder.State;

/// <summary>
/// Live snapshot of <see cref="DogfoodingNuGetServer"/> traffic the NuGet
/// analyzer tab renders. Kept hex1b-free so the (future) self-test command
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
