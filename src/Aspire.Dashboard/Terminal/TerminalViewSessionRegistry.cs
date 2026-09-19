// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Shares a component's per-view input policy with its terminal WebSocket handler.
/// </summary>
public sealed class TerminalViewSessionRegistry
{
    private readonly ConcurrentDictionary<string, TerminalViewSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a terminal view until the returned registration is disposed.
    /// </summary>
    /// <param name="endpointPathAndQuery">The resolved terminal endpoint, including any dashboard path base.</param>
    /// <param name="readOnly">Whether the view initially rejects input.</param>
    /// <returns>The component-owned registration.</returns>
    public TerminalViewSession Create(string endpointPathAndQuery, bool readOnly)
    {
        var session = new TerminalViewSession(endpointPathAndQuery, readOnly, id => _sessions.TryRemove(id, out _));
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException("A terminal view with the generated identifier already exists.");
        }

        return session;
    }

    /// <summary>
    /// Finds a live registration for the requested terminal endpoint.
    /// </summary>
    /// <param name="id">The view identifier supplied by the component.</param>
    /// <param name="endpointPathAndQuery">The requested endpoint, including its path base and optional viewId query parameter.</param>
    /// <param name="session">The matching registration.</param>
    /// <returns>Whether a matching registration exists.</returns>
    public bool TryGet(string id, string endpointPathAndQuery, [NotNullWhen(true)] out TerminalViewSession? session)
    {
        if (_sessions.TryGetValue(id, out var candidate) && candidate.MatchesEndpoint(endpointPathAndQuery))
        {
            session = candidate;
            return true;
        }

        session = null;
        return false;
    }
}

/// <summary>
/// Holds input policy for one browser view without owning the terminal or affecting other viewers.
/// </summary>
public sealed class TerminalViewSession : IDisposable
{
    private readonly string _endpointPath;
    private readonly Dictionary<string, StringValues> _endpointQuery;
    private readonly Action<string> _unregister;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readOnly;
    private int _disposed;

    internal TerminalViewSession(string endpointPathAndQuery, bool readOnly, Action<string> unregister)
    {
        var separator = endpointPathAndQuery.IndexOf('?');
        _endpointPath = separator < 0 ? endpointPathAndQuery : endpointPathAndQuery[..separator];
        _endpointQuery = QueryHelpers.ParseQuery(separator < 0 ? string.Empty : endpointPathAndQuery[separator..]);
        _endpointQuery.Remove("viewId");
        _readOnly = readOnly ? 1 : 0;
        _unregister = unregister;
    }

    /// <summary>
    /// Gets the opaque identifier that associates the component with its WebSocket connection.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets a task that completes only when the upstream explicitly reports producer completion.
    /// </summary>
    public Task Ended => _ended.Task;

    /// <summary>
    /// Gets or sets whether this view rejects input. Ended and disposed registrations always reject input.
    /// </summary>
    public bool ReadOnly
    {
        get => Volatile.Read(ref _disposed) != 0 || _ended.Task.IsCompleted || Volatile.Read(ref _readOnly) != 0;
        set => Volatile.Write(ref _readOnly, value ? 1 : 0);
    }

    /// <summary>
    /// Records authoritative producer completion without closing the browser view.
    /// </summary>
    public void MarkEnded() => _ended.TrySetResult();

    internal bool MatchesEndpoint(string endpointPathAndQuery)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        // Compare endpoints such as "/dashboard/api/terminal?resource=a%20b&replica=0&viewId=..."
        // semantically: query ordering and equivalent escaping must not invalidate a registration.
        var separator = endpointPathAndQuery.IndexOf('?');
        var path = separator < 0 ? endpointPathAndQuery : endpointPathAndQuery[..separator];
        if (!string.Equals(_endpointPath, path, StringComparison.Ordinal))
        {
            return false;
        }

        var query = QueryHelpers.ParseQuery(separator < 0 ? string.Empty : endpointPathAndQuery[separator..]);
        query.Remove("viewId");

        return _endpointQuery.Count == query.Count &&
            _endpointQuery.All(pair => query.TryGetValue(pair.Key, out var values) &&
                pair.Value.SequenceEqual(values, StringComparer.Ordinal));
    }

    /// <summary>
    /// Unregisters this view and disables any connection still holding its policy.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _unregister(Id);
        }
    }
}
