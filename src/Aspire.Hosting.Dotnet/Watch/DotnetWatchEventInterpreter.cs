// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

internal enum WatchNoticeKind
{
    Watching,
    HotReloadApplied,
    CompileError,
    Rebuilding,
    Restarting,
    BuildFailed,
    BuildSucceeded,
    Crashed,
    Relaunched,
    NoCodeChanges,
    ProjectFileChanged,
}

/// <summary>A concise, user-facing watch notice, attributed to the affected watched projects.</summary>
internal sealed record WatchNotice(WatchNoticeKind Kind, LogLevel Level, string Message, IReadOnlyList<string> ProjectPaths);

/// <summary>
/// SPIKE: turns the watcher's status events and console output into concise notices for the CLI and resource logs.
/// </summary>
/// <remarks>
/// The 10.0.x status protocol carries project paths only: it never fills in <c>error</c>, and it reports
/// <c>hot_reload_applied</c> for every running project even when the change didn't compile (or only touched one
/// project). The watcher's console output has the missing detail (changed files, the first compiler error, the
/// apply time), so notices are derived from both streams. Console lines arrive later than status events (they
/// flow through DCP), so notices that need an error message are held as pending until the first error line
/// arrives or <see cref="FlushPending"/> is called.
/// TODO(spike): replace the console parsing with structured status fields once the watcher provides them.
/// </remarks>
internal sealed partial class DotnetWatchEventInterpreter
{
    private readonly IReadOnlyList<(string ProjectPath, string Name)> _projects;
    private readonly List<string> _changedFiles = [];
    private readonly HashSet<string> _crashed = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _rebuilding;
    private bool _restartPending;
    private bool _inError;
    private string? _firstError;
    private string? _firstErrorFile;
    private PendingFailure? _pending;

    public DotnetWatchEventInterpreter(IEnumerable<(string ProjectPath, string Name)> projects)
    {
        _projects = projects.ToArray();
    }

    /// <summary>Whether a notice is waiting for an error line; call <see cref="FlushPending"/> after a short delay.</summary>
    public bool HasPending => _pending is not null;

    public IReadOnlyList<WatchNotice> OnStatus(string type, IReadOnlyList<string>? projects, bool? success, int? exitCode)
    {
        var notices = new List<WatchNotice>();
        projects ??= [];

        switch (type)
        {
            case "building":
                _firstError = null;
                _firstErrorFile = null;
                if (_ready)
                {
                    _rebuilding = true;
                    notices.Add(Notice(WatchNoticeKind.Rebuilding, LogLevel.Information, $"⌚ Rebuilding {Names(projects)}…", projects));
                }
                break;

            case "build_complete" when success == true:
                _rebuilding = false;
                _changedFiles.Clear();
                if (!_ready)
                {
                    _ready = true;
                    var recovered = _inError ? "✅ Build succeeded. " : "";
                    _inError = false;
                    _pending = null;
                    var all = _projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal).ToArray();
                    notices.Add(Notice(WatchNoticeKind.Watching, LogLevel.Information,
                        $"{recovered}👀 Watching {Names(all)} for changes. Edits are hot reloaded; changes that need a restart restart only the affected service.", all));
                }
                else if (_inError)
                {
                    _inError = false;
                    _pending = null;
                    notices.Add(Notice(WatchNoticeKind.BuildSucceeded, LogLevel.Information, $"✅ Build succeeded; {Names(projects)} resumed.", projects));
                }
                break;

            case "build_complete":
                _rebuilding = false;
                _restartPending = false;
                _inError = true;
                _pending = new PendingFailure(WatchNoticeKind.BuildFailed, projects, Initial: !_ready);
                TryCompletePending(notices);
                break;

            case "restarting":
                _rebuilding = false;
                notices.Add(Notice(WatchNoticeKind.Restarting, LogLevel.Information,
                    $"🔄 Restarting {Names(projects)} to apply changes that can't be hot reloaded.", projects));
                break;

            case "process_exited":
                foreach (var p in projects)
                {
                    _crashed.Add(p);
                }

                notices.Add(Notice(WatchNoticeKind.Crashed, LogLevel.Error,
                    $"💥 {Names(projects)} exited with code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}. Save a change or use Restart to relaunch it.", projects));
                break;

            case "process_started":
                var relaunched = projects.Where(_crashed.Remove).ToArray();
                if (relaunched.Length > 0)
                {
                    notices.Add(Notice(WatchNoticeKind.Relaunched, LogLevel.Information, $"▶️ {Names(relaunched)} is running again.", relaunched));
                }
                break;
        }

        return notices;
    }

    public IReadOnlyList<WatchNotice> OnLogLine(string line)
    {
        var notices = new List<WatchNotice>();

        if (FileChangeRegex().Match(line) is { Success: true } change)
        {
            if (_changedFiles.Count == 0)
            {
                _firstError = null;
                _firstErrorFile = null;
            }

            _changedFiles.AddRange(change.Groups["files"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (CompilerErrorRegex().Match(line) is { Success: true } error)
        {
            if (_firstError is null)
            {
                var file = error.Groups["file"].Value.Trim();
                _firstErrorFile = file;
                _firstError = string.Create(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(file)}({error.Groups["pos"].Value}): {error.Groups["code"].Value}: {error.Groups["msg"].Value.Trim()}");
            }

            TryCompletePending(notices);
        }
        else if (line.Contains("No managed code changes to apply", StringComparison.Ordinal))
        {
            var files = _changedFiles.ToArray();
            _changedFiles.Clear();
            if (files.Length > 0 && !TryAddProjectFileNotice(files, notices))
            {
                var projects = Attribute(files);
                notices.Add(Notice(WatchNoticeKind.NoCodeChanges, LogLevel.Information,
                    $"📝 {FileList(files)} changed in {Names(projects)}; no rebuild needed.", projects));
            }
        }
        else if (line.Contains("Restart is needed to apply the changes", StringComparison.Ordinal))
        {
            // The watcher reports the rebuild that follows a rude edit as "changes applied" once the restart is done;
            // the Rebuilding/Restarting notices from the status pipe already describe that cycle.
            _restartPending = true;
            _changedFiles.Clear();
        }
        else if (line.Contains("Unable to apply changes due to compilation errors", StringComparison.Ordinal))
        {
            _inError = true;
            _restartPending = false;
            _pending = new PendingFailure(WatchNoticeKind.CompileError, Attribute(_changedFiles), Initial: false);
            _changedFiles.Clear();
            TryCompletePending(notices);
        }
        else if (AppliedRegex().Match(line) is { Success: true } applied)
        {
            var files = _changedFiles.ToArray();
            _changedFiles.Clear();

            // A rebuild (rude edit, project change) also logs "changes applied" for the projects it didn't restart;
            // the Rebuilding/Restarting notices already describe that cycle.
            if (_rebuilding || _restartPending)
            {
                _restartPending = false;
                return notices;
            }

            // A crashed project is relaunched rather than patched; the Relaunched notice covers it.
            var projects = Attribute(files).Where(p => !_crashed.Contains(p)).ToArray();
            if (projects.Length == 0)
            {
                return notices;
            }

            var fileList = files.Length == 0 ? "" : $" ({FileList(files)})";
            var prefix = _inError ? "✅ Fixed. " : "";
            _inError = false;
            _pending = null;
            notices.Add(Notice(WatchNoticeKind.HotReloadApplied, LogLevel.Information,
                $"{prefix}🔥 Hot reload applied to {Names(projects)} in {applied.Groups["ms"].Value} ms{fileList}.", projects));
            TryAddProjectFileNotice(files, notices);
        }

        return notices;
    }

    /// <summary>Emits a pending failure without an error line (for example when the output was not captured).</summary>
    public IReadOnlyList<WatchNotice> FlushPending()
    {
        var notices = new List<WatchNotice>();
        if (_pending is not null)
        {
            notices.Add(CreateFailureNotice(_pending, error: null));
            _pending = null;
        }

        return notices;
    }

    /// <summary>The first error of the current build or edit, for health/status text.</summary>
    public string? FirstError => _firstError;

    private void TryCompletePending(List<WatchNotice> notices)
    {
        if (_pending is not null && _firstError is not null)
        {
            var pending = _pending;
            if (pending.Kind == WatchNoticeKind.CompileError && _firstErrorFile is { } file && Attribute([file]) is { Count: > 0 } owners && owners.Count < _projects.Count)
            {
                pending = pending with { Projects = owners };
            }

            notices.Add(CreateFailureNotice(pending, _firstError));
            _pending = null;
        }
    }

    private WatchNotice CreateFailureNotice(PendingFailure pending, string? error)
    {
        var detail = error ?? "see the 'dotnet-watch' resource logs for details";
        return pending switch
        {
            { Kind: WatchNoticeKind.CompileError } => Notice(WatchNoticeKind.CompileError, LogLevel.Error,
                $"❌ Build error in {Names(pending.Projects)}: {detail}. The app keeps running the last good code; fix it and save.", pending.Projects),
            { Initial: true } => Notice(WatchNoticeKind.BuildFailed, LogLevel.Error,
                $"❌ Initial build failed: {detail}. Fix it and save; {Names(pending.Projects)} start once the build succeeds.", pending.Projects),
            _ => Notice(WatchNoticeKind.BuildFailed, LogLevel.Error,
                $"❌ Build failed for {Names(pending.Projects)}: {detail}. Fix it and save.", pending.Projects),
        };
    }

    /// <summary>
    /// Maps changed files to the watched projects whose directory contains them; files outside every project (a
    /// shared library, for example) affect all watched projects.
    /// </summary>
    private IReadOnlyList<string> Attribute(IReadOnlyCollection<string> files)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var matched = false;
            if (Path.IsPathRooted(file))
            {
                foreach (var (projectPath, _) in _projects)
                {
                    var dir = Path.GetDirectoryName(projectPath)! + Path.DirectorySeparatorChar;
                    if (file.StartsWith(dir, StringComparison.Ordinal))
                    {
                        owners.Add(projectPath);
                        matched = true;
                    }
                }
            }

            if (!matched)
            {
                return _projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal).ToArray();
            }
        }

        return owners.Count > 0 ? owners.ToArray() : _projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The watcher reloads a changed project file but doesn't rebuild or restart the app, so a new package or project
    /// reference isn't deployed (the app fails with FileNotFoundException when it's first used). Point to Rebuild.
    /// </summary>
    private bool TryAddProjectFileNotice(IReadOnlyCollection<string> files, List<WatchNotice> notices)
    {
        var projectFiles = files.Where(IsProjectFile).ToArray();
        if (projectFiles.Length == 0)
        {
            return false;
        }

        var projects = Attribute(projectFiles);
        var names = Names(projects);
        notices.Add(Notice(WatchNoticeKind.ProjectFileChanged, LogLevel.Warning,
            $"📦 {FileList(projectFiles)} changed. New package or project references aren't hot reloaded; use Rebuild on {names} (dashboard, or 'aspire resource {(projects.Count == 1 ? names : "<name>")} rebuild') to apply them.", projects));
        return true;
    }

    private static bool IsProjectFile(string file) =>
        Path.GetExtension(file) is { } ext &&
        (ext.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || ext.Equals(".props", StringComparison.OrdinalIgnoreCase) || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase));

    private static string FileList(IEnumerable<string> files) => string.Join(", ", files.Select(Path.GetFileName).Distinct(StringComparer.Ordinal));

    private string Names(IEnumerable<string> projectPaths)
    {
        var names = projectPaths
            .SelectMany(path => _projects.Where(p => string.Equals(p.ProjectPath, path, StringComparison.Ordinal)).Select(p => p.Name).DefaultIfEmpty(Path.GetFileNameWithoutExtension(path)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? "services" : string.Join(", ", names);
    }

    private static WatchNotice Notice(WatchNoticeKind kind, LogLevel level, string message, IReadOnlyList<string> projects) => new(kind, level, message, projects);

    private sealed record PendingFailure(WatchNoticeKind Kind, IReadOnlyList<string> Projects, bool Initial);

    [GeneratedRegex(@"\bFiles? (?:updated|added|deleted): (?<files>.+)$")]
    private static partial Regex FileChangeRegex();

    // MSBuild/Roslyn diagnostic: path(line,col): error CODE: message [project]
    [GeneratedRegex(@"(?:^|\s)(?<file>(?:[A-Za-z]:)?[\\/][^(]*?|[^\s(\\/][^\s(]*)\((?<pos>\d+,\d+)\): error (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex CompilerErrorRegex();

    [GeneratedRegex(@"changes applied in (?<ms>\d+)ms")]
    private static partial Regex AppliedRegex();
}
