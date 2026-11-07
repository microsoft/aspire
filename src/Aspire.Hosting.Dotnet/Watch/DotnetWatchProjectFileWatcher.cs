// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Watches evaluated project/import inputs and SDK/build-scope files, reporting changes once saves settle.
/// </summary>
/// <remarks>
/// The watcher (10.0.x) re-evaluates and restores a changed project but doesn't rebuild it, so a new package is never
/// deployed: code that uses it is hot reloaded and then fails with FileNotFoundException. The protocol has no event
/// for a project file change, so the coordinator observes these files itself and restarts the watcher, whose initial
/// build deploys the change.
/// </remarks>
internal sealed class DotnetWatchProjectFileWatcher : IDisposable
{
    private static readonly string[] s_ancestorFiles =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config"];

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, bool> _directories;
    private readonly Action<IReadOnlyList<string>> _onChanged;
    private readonly Action<Exception> _onError;
    private readonly HashSet<string> _inputs;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private Timer? _timer;
    private bool _disposed;

    /// <param name="projectPaths">Evaluated physical project and import paths, including file-app entry points.</param>
    /// <param name="onChanged">Called with the changed files, once no change happened for <paramref name="debounce"/>.</param>
    /// <param name="debounce">How long saves must settle.</param>
    /// <param name="onError">Reports lost observation or infrastructure failure to the owning group.</param>
    public DotnetWatchProjectFileWatcher(
        IEnumerable<string> projectPaths, Action<IReadOnlyList<string>> onChanged, TimeSpan debounce, Action<Exception> onError)
    {
        _onChanged = onChanged;
        _onError = onError;
        _debounce = debounce;
        _inputs = projectPaths.Select(Path.GetFullPath).ToHashSet(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        _directories = GetWatchedDirectories(_inputs);
    }

    /// <summary>The directories to watch, and whether only the <c>Directory.*</c> files count in each.</summary>
    internal static Dictionary<string, bool> GetWatchedDirectories(IEnumerable<string> projectPaths)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var directories = new Dictionary<string, bool>(comparer);
        foreach (var projectPath in projectPaths)
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            directories[projectDirectory] = false;
        }

        foreach (var directory in directories.Keys.ToArray())
        {
            for (var parent = Path.GetDirectoryName(directory); parent is not null; parent = Path.GetDirectoryName(parent))
            {
                directories.TryAdd(parent, true);
            }
        }

        return directories;
    }

    /// <summary>Whether a change to <paramref name="path"/> can change what the watched projects build or reference.</summary>
    internal static bool IsBuildFile(string path, bool ancestorOnly)
    {
        var name = Path.GetFileName(path);
        if (s_ancestorFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !ancestorOnly && Path.GetExtension(name) is { } extension &&
            (extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (directory, ancestorOnly) in _directories)
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler handler = (_, e) => OnChange(e.FullPath, ancestorOnly);
                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += (_, e) =>
                {
                    OnChange(e.OldFullPath, ancestorOnly);
                    OnChange(e.FullPath, ancestorOnly);
                };
                watcher.Error += (_, e) =>
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            _onError(e.GetException());
                        }
                    }
                };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
    }

    private void OnChange(string path, bool ancestorOnly)
    {
        if (!IsBuildFile(path, ancestorOnly) && !_inputs.Contains(Path.GetFullPath(path)))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(path);
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            var changed = _pending.Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToArray();
            _pending.Clear();
            _onChanged(changed);
        }
    }

    /// <summary>Stops observation and returns queued edits for transfer to a replacement inventory.</summary>
    internal string[] DisposeAndGetPendingChanges()
    {
        string[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }
            _disposed = true;
            _timer?.Dispose();
            pending = [.. _pending.Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)];
            _pending.Clear();
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        return pending;
    }

    public void Dispose() => DisposeAndGetPendingChanges();
}
