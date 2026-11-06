// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// SPIKE: watches the MSBuild files of a watch server's projects (<c>*.*proj</c>, <c>*.props</c>, <c>*.targets</c> in
/// the project directories and the directories of the projects they reference, and <c>Directory.Build.props</c>,
/// <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c> above them) and reports changes once saves settle.
/// </summary>
/// <remarks>
/// The watcher (10.0.x) re-evaluates and restores a changed project but doesn't rebuild it, so a new package is never
/// deployed: code that uses it is hot reloaded and then fails with FileNotFoundException. The protocol has no event
/// for a project file change, so the coordinator observes these files itself and restarts the watcher, whose initial
/// build deploys the change.
/// </remarks>
internal sealed class DotnetWatchProjectFileWatcher : IDisposable
{
    private static readonly string[] s_ancestorFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, bool> _directories;
    private readonly Action<IReadOnlyList<string>> _onChanged;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private Timer? _timer;
    private bool _disposed;

    /// <param name="projectPaths">The watched projects (project files or file-based app entry points).</param>
    /// <param name="onChanged">Called with the changed files, once no change happened for <paramref name="debounce"/>.</param>
    /// <param name="debounce">How long saves must settle.</param>
    public DotnetWatchProjectFileWatcher(IEnumerable<string> projectPaths, Action<IReadOnlyList<string>> onChanged, TimeSpan debounce)
    {
        _onChanged = onChanged;
        _debounce = debounce;
        _directories = GetWatchedDirectories(projectPaths);
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
            foreach (var referenced in ReadReferencedDirectories(projectPath))
            {
                directories[Path.TrimEndingDirectorySeparator(referenced)] = false;
            }
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

    /// <summary>
    /// Best-effort: the directories of the projects <paramref name="projectPath"/> references through literal
    /// <c>ProjectReference</c> items, transitively. References computed by MSBuild (properties, imports) aren't seen.
    /// </summary>
    internal static IReadOnlyList<string> ReadReferencedDirectories(string projectPath)
    {
        var directories = new List<string>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { Path.GetFullPath(projectPath) };
        var queue = new Queue<string>(visited);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IEnumerable<string> includes;
            try
            {
                includes = XDocument.Load(current).Descendants()
                    .Where(e => e.Name.LocalName == "ProjectReference")
                    .Select(e => (string?)e.Attribute("Include"))
                    .OfType<string>()
                    .SelectMany(i => i.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(i => !i.Contains("$(", StringComparison.Ordinal) && !i.Contains('*'))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }

            foreach (var include in includes)
            {
                var referenced = Path.GetFullPath(include.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(current)!);
                if (visited.Add(referenced) && File.Exists(referenced))
                {
                    directories.Add(Path.GetDirectoryName(referenced)! + Path.DirectorySeparatorChar);
                    queue.Enqueue(referenced);
                }
            }
        }

        return directories;
    }

    public void Start()
    {
        foreach (var (directory, ancestorOnly) in _directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
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
                watcher.Renamed += (_, e) => OnChange(e.FullPath, ancestorOnly);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Best effort, e.g. an unreadable ancestor directory.
            }
        }
    }

    private void OnChange(string path, bool ancestorOnly)
    {
        if (!IsBuildFile(path, ancestorOnly))
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
        string[] changed;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            changed = [.. _pending.Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)];
            _pending.Clear();
        }

        _onChanged(changed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}
