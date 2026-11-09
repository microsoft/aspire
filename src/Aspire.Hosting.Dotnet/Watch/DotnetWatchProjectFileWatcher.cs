// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Polls evaluated project/import inputs and SDK/build-scope files, reporting content changes once saves settle.
/// </summary>
/// <remarks>
/// The watcher (10.0.x) re-evaluates and restores a changed project but doesn't rebuild it, so a new package is never
/// deployed: code that uses it is hot reloaded and then fails with FileNotFoundException. The protocol has no event
/// for a project file change, so the coordinator observes the finite evaluated input set itself and restarts the
/// watcher, whose initial build deploys the change.
/// </remarks>
internal sealed class DotnetWatchProjectFileWatcher : IDisposable
{
    private static readonly string[] s_ancestorFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"];

    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Action<IReadOnlyList<string>> _onChanged;
    private readonly Action<Exception> _onError;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly HashSet<string> _candidates;
    private readonly Dictionary<string, FileState> _states = new(s_pathComparer);
    private readonly HashSet<string> _pending = new(s_pathComparer);
    private CancellationTokenSource? _stop;
    private Task? _pollTask;
    private DateTimeOffset? _flushAfter;
    private bool _disposed;

    /// <param name="projectPaths">Evaluated physical project and import paths, including file-app entry points.</param>
    /// <param name="onChanged">Called with changed files after no further change occurs for <paramref name="debounce"/>.</param>
    /// <param name="debounce">How long saves must settle.</param>
    /// <param name="onError">Reports a polling failure; observation continues on the next tick.</param>
    /// <param name="pollInterval">Optional polling interval used by tests.</param>
    public DotnetWatchProjectFileWatcher(
        IEnumerable<string> projectPaths,
        Action<IReadOnlyList<string>> onChanged,
        TimeSpan debounce,
        Action<Exception> onError,
        TimeSpan? pollInterval = null)
    {
        _onChanged = onChanged;
        _onError = onError;
        _debounce = debounce;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _candidates = GetObservedFiles(projectPaths).ToHashSet(s_pathComparer);
    }

    /// <summary>
    /// Returns evaluated inputs plus the ancestor files whose creation, deletion, or content can change SDK/build scope.
    /// </summary>
    internal static string[] GetObservedFiles(IEnumerable<string> projectPaths)
    {
        var files = new HashSet<string>(s_pathComparer);
        foreach (var projectPath in projectPaths)
        {
            var fullPath = Path.GetFullPath(projectPath);
            files.Add(fullPath);
            for (var directory = Path.GetDirectoryName(fullPath);
                directory is not null;
                directory = Path.GetDirectoryName(directory))
            {
                foreach (var name in s_ancestorFiles)
                {
                    files.Add(Path.Combine(directory, name));
                }
            }
        }

        return [.. files];
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pollTask is not null)
            {
                throw new InvalidOperationException("Project-file observation has already started.");
            }

            using var observationActivity = DotnetWatchProfiling.StartInputObservation(_candidates, _pollInterval);
            foreach (var path in _candidates)
            {
                if (TryReadState(path, previous: null, out var state))
                {
                    _states[path] = state;
                }
            }

            _stop = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollAsync(_stop.Token), CancellationToken.None);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PollOnce();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PollOnce()
    {
        List<string>? changed = null;
        foreach (var path in _candidates)
        {
            _states.TryGetValue(path, out var previous);
            if (!TryReadState(path, previous, out var current))
            {
                continue;
            }

            _states[path] = current;
            if (previous is not null && previous.ContentHash != current.ContentHash)
            {
                changed ??= [];
                changed.Add(path);
            }
        }

        IReadOnlyList<string>? settled = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (changed is not null)
            {
                _pending.UnionWith(changed);
                _flushAfter = DateTimeOffset.UtcNow + _debounce;
            }

            if (_pending.Count > 0 && DateTimeOffset.UtcNow >= _flushAfter)
            {
                settled = [.. _pending];
                _pending.Clear();
                _flushAfter = null;
            }
        }

        if (settled is not null)
        {
            _onChanged(settled);
        }
    }

    private bool TryReadState(string path, FileState? previous, out FileState state)
    {
        try
        {
            var info = new FileInfo(path);
            var metadata = info.Exists
                ? new FileMetadata(true, info.Length, info.LastWriteTimeUtc.Ticks)
                : FileMetadata.Missing;
            if (previous is not null && previous.Metadata == metadata)
            {
                state = previous;
                return true;
            }

            // Metadata checks keep the steady-state poll cheap. Hash only after existence, size, or write time changes
            // so timestamp-only touches do not force a guarded refresh.
            ulong? hash = metadata.Exists ? XxHash3.HashToUInt64(File.ReadAllBytes(path)) : null;
            state = new(metadata, hash);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _onError(exception);
            state = previous ?? new(FileMetadata.Missing, ContentHash: null);
            return false;
        }
    }

    /// <summary>Stops observation and returns queued edits for transfer to a replacement inventory.</summary>
    internal string[] DisposeAndGetPendingChanges()
    {
        CancellationTokenSource? stop;
        Task? pollTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }
            stop = _stop;
            pollTask = _pollTask;
        }

        stop?.Cancel();
        try
        {
            pollTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (stop?.IsCancellationRequested == true)
        {
        }

        // Capture a final metadata transition that occurred after the last timer tick.
        PollOnce();

        lock (_gate)
        {
            _disposed = true;
            var pending = _pending.ToArray();
            _pending.Clear();
            _flushAfter = null;
            stop?.Dispose();
            return pending;
        }
    }

    public void Dispose() => DisposeAndGetPendingChanges();

    private sealed record FileState(FileMetadata Metadata, ulong? ContentHash);

    private readonly record struct FileMetadata(bool Exists, long Length, long LastWriteTicks)
    {
        internal static FileMetadata Missing => new(false, 0, 0);
    }
}
