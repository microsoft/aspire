// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <remarks>The values are part of the notice event id (see <c>DotnetWatchCoordinator.NoticeEventId</c>); don't renumber them.</remarks>
internal enum WatchNoticeKind
{
    Watching = 0,
    HotReloadApplied = 1,
    /// <summary>Retired: the status protocol has no compile-error event.</summary>
    CompileError = 2,
    Rebuilding = 3,
    Restarting = 4,
    BuildFailed = 5,
    BuildSucceeded = 6,
    Crashed = 7,
    Relaunched = 8,
    /// <summary>Retired: the status protocol has no "no changes" event.</summary>
    NoCodeChanges = 9,
    ProjectFileChanged = 10,
}

/// <summary>A concise, user-facing watch notice, attributed to the affected watched projects.</summary>
internal sealed record WatchNotice(WatchNoticeKind Kind, LogLevel Level, string Message, IReadOnlyList<string> ProjectPaths);

/// <summary>
/// Event types of the watcher's structured status pipe (<c>--status-pipe</c>): one JSON object per line with
/// <c>type</c>, <c>projects</c> and optional <c>success</c>, <c>error</c> and <c>exitCode</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>WatchStatusEvent.Types</c> in dotnet/sdk (src/Dotnet.Watch/Watch.Aspire/Server/WatchStatusEvent.cs). The
/// build/apply/restart events are raised from the watcher's log notifications (<c>StatusReportingLoggerFactory</c>);
/// the process events from the resource launcher.
/// </remarks>
internal static class DotnetWatchStatusTypes
{
    public const string Building = "building";
    public const string BuildComplete = "build_complete";
    public const string HotReloadApplied = "hot_reload_applied";
    public const string Restarting = "restarting";
    public const string ProcessExited = "process_exited";
    public const string ProcessStarted = "process_started";
}

/// <summary>
/// Turns the watcher's status events (<c>--status-pipe</c>) into concise notices for the CLI and resource logs.
/// The watcher's console output is shown as logs of the <c>dotnet-watch</c> resource and never interpreted.
/// </summary>
/// <remarks>
/// <para>
/// The 10.0.x protocol carries event types and project paths only, which limits what the notices can say:
/// </para>
/// <list type="bullet">
/// <item><description><c>error</c> is never set, so a failed build points to the <c>dotnet-watch</c> logs instead of
/// naming the first error (a future watcher that fills it in gets it shown).</description></item>
/// <item><description><c>hot_reload_applied</c> follows every hot reload attempt: applied, blocked by compile errors and
/// "no changes" alike, for every running project. So the notice says the edit was processed, not that it applied, and
/// a compile error during a session has no notice of its own.</description></item>
/// <item><description>No changed files, apply time, or replica identity.</description></item>
/// </list>
/// TODO(upstream): richer notices once the watcher reports them (see the dotnet/sdk issue linked from the PR).
/// </remarks>
internal sealed class DotnetWatchEventInterpreter
{
    private readonly IReadOnlyList<(string ProjectPath, string Name)> _projects;
    private readonly string _watchResourceName;
    private readonly HashSet<string> _crashed = new(DotnetWatchProjectGraph.PathComparer);
    private bool _ready;
    private bool _inError;

    /// <param name="projects">The watched projects and their resource names.</param>
    /// <param name="watchResourceName">The exact hidden watch resource that owns the console output.</param>
    public DotnetWatchEventInterpreter(
        IEnumerable<(string ProjectPath, string Name)> projects,
        string watchResourceName)
    {
        _projects = projects.Select(project =>
            (DotnetWatchProjectGraph.Normalize(project.ProjectPath), project.Name)).ToArray();
        _watchResourceName = watchResourceName;
    }

    /// <summary>The detail shown for a failed build: the event's <c>error</c>, or a pointer to the watcher's logs.</summary>
    public static string FailureDetail(string? error, string watchResourceName) =>
        string.IsNullOrWhiteSpace(error) ? $"see the '{watchResourceName}' resource logs for the errors" : error;

    public IReadOnlyList<WatchNotice> OnStatus(string type, IReadOnlyList<string>? projects, bool? success, int? exitCode, string? error = null)
    {
        var notices = new List<WatchNotice>();
        projects = (projects ?? [])
            .Select(DotnetWatchProjectGraph.Normalize)
            .Distinct(DotnetWatchProjectGraph.PathComparer)
            .ToArray();

        switch (type)
        {
            case DotnetWatchStatusTypes.Building:
                if (_ready)
                {
                    notices.Add(Notice(WatchNoticeKind.Rebuilding, LogLevel.Information, $"⌚ Rebuilding {Names(projects)}…", projects));
                }
                break;

            case DotnetWatchStatusTypes.BuildComplete when success == true:
                if (!_ready)
                {
                    _ready = true;
                    var recovered = _inError ? "✅ Build succeeded. " : "";
                    _inError = false;
                    var all = _projects.Select(p => p.ProjectPath).Distinct(DotnetWatchProjectGraph.PathComparer).ToArray();
                    notices.Add(Notice(WatchNoticeKind.Watching, LogLevel.Information,
                        $"{recovered}👀 Watching {Names(all)} for changes. Edits are hot reloaded; changes that need a restart restart only the affected service.", all));
                }
                else if (_inError)
                {
                    _inError = false;
                    notices.Add(Notice(WatchNoticeKind.BuildSucceeded, LogLevel.Information, $"✅ Build succeeded for {Names(projects)}.", projects));
                }
                break;

            case DotnetWatchStatusTypes.BuildComplete:
                _inError = true;
                var detail = FailureDetail(error, _watchResourceName);
                notices.Add(_ready
                    ? Notice(WatchNoticeKind.BuildFailed, LogLevel.Error, $"❌ Build failed for {Names(projects)}: {detail}. Fix it and save.", projects)
                    : Notice(WatchNoticeKind.BuildFailed, LogLevel.Error, $"❌ Initial build failed: {detail}. Fix it and save; {Names(projects)} start once the build succeeds.", projects));
                break;

            case DotnetWatchStatusTypes.HotReloadApplied:
                // Empty during a rebuild cycle; a crashed project is relaunched rather than patched (Relaunched covers it).
                var updated = projects.Where(p => !_crashed.Contains(p)).ToArray();
                if (updated.Length > 0)
                {
                    notices.Add(Notice(WatchNoticeKind.HotReloadApplied, LogLevel.Information,
                        $"⌚ The watcher processed the change for {Names(updated)}. Watcher 10.0.403 does not report whether hot reload applied. Open '{_watchResourceName}' Console logs in the dashboard, or run 'aspire logs {_watchResourceName} --tail 50'.", updated));
                }
                break;

            case DotnetWatchStatusTypes.Restarting:
                if (projects.Count > 0)
                {
                    notices.Add(Notice(WatchNoticeKind.Restarting, LogLevel.Information, $"🔄 Restarting {Names(projects)}…", projects));
                }
                break;

            case DotnetWatchStatusTypes.ProcessExited:
                _crashed.UnionWith(projects);
                notices.Add(Notice(WatchNoticeKind.Crashed, LogLevel.Error,
                    $"💥 {Names(projects)} exited with code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}. Save a change or use Restart to relaunch it.", projects));
                break;

            case DotnetWatchStatusTypes.ProcessStarted:
                var relaunched = projects.Where(_crashed.Remove).ToArray();
                if (relaunched.Length > 0)
                {
                    notices.Add(Notice(WatchNoticeKind.Relaunched, LogLevel.Information, $"▶️ {Names(relaunched)} is running again.", relaunched));
                }
                break;
        }

        return notices;
    }

    private string Names(IEnumerable<string> projectPaths)
    {
        var names = projectPaths
            .SelectMany(path => _projects.Where(p => DotnetWatchProjectGraph.PathComparer.Equals(p.ProjectPath, path))
                .Select(p => p.Name).DefaultIfEmpty(Path.GetFileNameWithoutExtension(path)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? "services" : string.Join(", ", names);
    }

    private static WatchNotice Notice(WatchNoticeKind kind, LogLevel level, string message, IReadOnlyList<string> projects) => new(kind, level, message, projects);
}
