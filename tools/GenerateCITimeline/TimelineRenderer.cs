// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record JobInfo(
    string Name,
    string Status,
    string Conclusion,
    double CreatedAt,
    double StartedAt,
    double CompletedAt,
    string RunnerName,
    string HtmlUrl,
    List<string> Labels)
{
    public double QueueTime => Math.Max(0, StartedAt - CreatedAt);
    public double RunTime => Math.Max(0, CompletedAt - StartedAt);
}

internal sealed class JobGroup(string name)
{
    public string Name { get; } = name;
    public List<JobInfo> Jobs { get; } = [];

    public double FirstCreated => Jobs.Min(j => j.CreatedAt);
    public double FirstStarted => Jobs.Min(j => j.StartedAt);
    public double LastCompleted => Jobs.Max(j => j.CompletedAt);
    public string GroupConclusion
    {
        get
        {
            var conclusions = Jobs.Select(j => j.Conclusion).ToHashSet();
            if (conclusions.Contains("failure"))
            {
                return "failure";
            }
            if (conclusions.Contains("cancelled"))
            {
                return "cancelled";
            }
            if (conclusions.Count == 1 && conclusions.Contains("skipped"))
            {
                return "skipped";
            }
            return "success";
        }
    }
}

internal static partial class TimelineRenderer
{
    private static readonly Dictionary<string, string> s_statusIcon = new()
    {
        ["success"] = "✅",
        ["failure"] = "❌",
        ["cancelled"] = "⚪",
        ["skipped"] = "⏭️",
    };

    private static readonly Dictionary<string, string> s_runnerEmoji = new()
    {
        ["ubuntu"] = "🐧",
        ["windows"] = "🪟",
        ["macos"] = "🍎",
    };

    private static readonly string[] s_phaseOrder = ["Setup", "Build", "Tests", "Templates", "Validation"];

    private static string FormatDuration(double seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60)
        {
            return $"{seconds:0}s";
        }

        var minutes = (int)(seconds / 60);
        var secs = (int)(seconds % 60);
        if (minutes < 60)
        {
            return $"{minutes}m{secs:00}s";
        }

        var hours = minutes / 60;
        var mins = minutes % 60;
        return $"{hours}h{mins:00}m";
    }

    private static string GetRunnerIcon(string label)
    {
        var lower = label.ToLowerInvariant();
        foreach (var (key, emoji) in s_runnerEmoji)
        {
            if (lower.Contains(key, StringComparison.Ordinal))
            {
                if (lower.Contains("8-core", StringComparison.Ordinal) || lower.Contains("16-core", StringComparison.Ordinal))
                {
                    return emoji + "⚡";
                }
                return emoji;
            }
        }
        return "🖥️";
    }

    private static string ClassifyPhase(string groupName)
    {
        var lower = groupName.ToLowerInvariant();
        if (lower.Contains("prepare") || lower.Contains("setup"))
        {
            return "Setup";
        }
        if (lower.Contains("build") && !lower.Contains("template"))
        {
            return "Build";
        }
        if (lower.Contains("template"))
        {
            return "Templates";
        }
        if (lower.Contains("polyglot") || lower.Contains("sdk validation"))
        {
            return "Validation";
        }
        if (lower.Contains("cli starter"))
        {
            return "Validation";
        }
        if (lower.Contains("vs code extension") || lower.Contains("extension tests"))
        {
            return "Validation";
        }
        if (lower.Contains("java sdk unit") || lower.Contains("typescript sdk unit"))
        {
            return "Validation";
        }
        if (lower.Contains("final") || lower.Contains("results"))
        {
            return "Results";
        }
        return "Tests";
    }

    private static bool IsResultsPhase(JobGroup g) => ClassifyPhase(g.Name) == "Results";

    [GeneratedRegex(@"\s*\(.*\)")]
    private static partial Regex MatrixParamsRegex();

    [GeneratedRegex(@"^Tests\s*/\s*")]
    private static partial Regex TestsPrefixRegex();

    private static string GroupBaseName(string jobName) =>
        MatrixParamsRegex().Replace(jobName, "").Trim();

    private static string ShortName(string groupName)
    {
        var name = TestsPrefixRegex().Replace(groupName, "");
        var parts = name.Split('/').Select(p => p.Trim()).ToArray();
        if (parts.Length == 2 && parts[0] == parts[1])
        {
            name = parts[0];
        }
        return name;
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static DateTimeOffset? ParseTs(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            if (s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                return dto;
            }
        }
        return null;
    }

    private static string GetString(JsonElement element, string property, string defaultValue = "")
    {
        if (element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString() ?? defaultValue;
        }
        return defaultValue;
    }

    private static int GetInt(JsonElement element, string property, int defaultValue = 0)
    {
        if (element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            return val.GetInt32();
        }
        return defaultValue;
    }

    private static List<string> GetLabels(JsonElement job)
    {
        var labels = new List<string>();
        if (job.TryGetProperty("labels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    labels.Add(item.GetString()!);
                }
            }
        }
        return labels;
    }

    private static List<JobGroup> BuildJobGroups(List<JsonElement> jobs, DateTimeOffset t0)
    {
        var groups = new Dictionary<string, JobGroup>();

        foreach (var raw in jobs)
        {
            var created = ParseTs(raw, "created_at");
            var started = ParseTs(raw, "started_at");
            var completed = ParseTs(raw, "completed_at");

            if (started is null || completed is null)
            {
                continue;
            }

            // Skip jobs from a previous attempt
            if (created is not null && started < created)
            {
                continue;
            }

            var createdSecs = created is not null
                ? (created.Value - t0).TotalSeconds
                : (started.Value - t0).TotalSeconds;

            var job = new JobInfo(
                Name: GetString(raw, "name"),
                Status: GetString(raw, "status"),
                Conclusion: GetString(raw, "conclusion"),
                CreatedAt: createdSecs,
                StartedAt: (started.Value - t0).TotalSeconds,
                CompletedAt: (completed.Value - t0).TotalSeconds,
                RunnerName: GetString(raw, "runner_name"),
                HtmlUrl: GetString(raw, "html_url"),
                Labels: GetLabels(raw));

            var baseName = GroupBaseName(GetString(raw, "name"));
            if (!groups.TryGetValue(baseName, out var group))
            {
                group = new JobGroup(baseName);
                groups[baseName] = group;
            }
            group.Jobs.Add(job);
        }

        return [.. groups.Values.OrderBy(g => g.FirstStarted)];
    }

    private static bool IsSignificantJob(JobInfo j) =>
        j.RunTime >= 60 || j.Conclusion is "failure" or "cancelled";

    private static string RenderSummaryTable(List<JobGroup> groups, double totalSeconds)
    {
        var allJobs = groups.SelectMany(g => g.Jobs).ToList();
        if (allJobs.Count == 0)
        {
            return "";
        }

        var conclusions = allJobs.GroupBy(j => j.Conclusion).ToDictionary(g => g.Key, g => g.Count());
        var runnerLabels = allJobs.SelectMany(j => j.Labels).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());

        var statusParts = new List<string>();
        foreach (var status in new[] { "success", "failure", "cancelled", "skipped" })
        {
            if (conclusions.TryGetValue(status, out var count) && count > 0)
            {
                var icon = s_statusIcon.GetValueOrDefault(status, "");
                statusParts.Add($"{icon} {count} {status}");
            }
        }

        var labelParts = runnerLabels
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"<code>{E(kv.Key)}</code>: {kv.Value}")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Metric</th><th>Value</th></tr>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>Total wall time</td><td><b>{FormatDuration(totalSeconds)}</b></td></tr>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>Jobs</td><td>{allJobs.Count} ({groups.Count} unique)</td></tr>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>Status</td><td>{string.Join(" · ", statusParts)}</td></tr>");
        if (labelParts.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>Runners</td><td>{string.Join(" · ", labelParts)}</td></tr>");
        }
        sb.AppendLine("</table>");

        return sb.ToString();
    }

    private static string RenderCriticalPath(List<JobGroup> groups, double minTotalSeconds = 300)
    {
        if (groups.Count == 0)
        {
            return "";
        }

        var filteredGroups = groups.Where(g => !IsResultsPhase(g)).ToList();
        var threshold = Math.Max(minTotalSeconds, 300);
        var allJobs = filteredGroups
            .SelectMany(g => g.Jobs)
            .Where(j => j.CompletedAt > threshold && IsSignificantJob(j))
            .OrderByDescending(j => j.CompletedAt)
            .Take(30)
            .ToList();

        if (allJobs.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>#</th><th>Job</th><th>Total</th><th>Deps</th><th>Queue</th><th>Run</th></tr>");

        for (var i = 0; i < allJobs.Count; i++)
        {
            var j = allJobs[i];
            var name = E(ShortName(GroupBaseName(j.Name)));
            var icon = s_statusIcon.GetValueOrDefault(j.Conclusion, "");
            var runner = j.Labels.Count > 0 ? j.Labels[0] : "";
            var ri = runner.Length > 0 ? GetRunnerIcon(runner) + " " : "";

            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{i + 1}</td><td>{icon} {ri}<code>{name}</code></td>"
                + $"<td><b>{FormatDuration(j.CompletedAt)}</b></td>"
                + $"<td>{FormatDuration(j.CreatedAt)}</td>"
                + $"<td>{FormatDuration(j.QueueTime)}</td>"
                + $"<td>{FormatDuration(j.RunTime)}</td></tr>");
        }

        sb.AppendLine("</table>");
        return sb.ToString();
    }

    private static string RenderHotspots(List<JobGroup> groups)
    {
        var filteredGroups = groups.Where(g => !IsResultsPhase(g)).ToList();
        var allJobs = filteredGroups.SelectMany(g => g.Jobs).ToList();
        if (allJobs.Count == 0)
        {
            return "";
        }

        var worstJobs = filteredGroups
            .SelectMany(g => g.Jobs)
            .OrderByDescending(j => j.QueueTime)
            .Take(10)
            .Where(j => j.QueueTime > 30)
            .ToList();

        if (worstJobs.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Job</th><th>Queue</th></tr>");

        foreach (var j in worstJobs)
        {
            var name = E(ShortName(GroupBaseName(j.Name)));
            var runner = j.Labels.Count > 0 ? j.Labels[0] : "";
            var ri = runner.Length > 0 ? GetRunnerIcon(runner) + " " : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>⏳ {ri}<code>{name}</code></td><td><b>{FormatDuration(j.QueueTime)}</b></td></tr>");
        }

        sb.AppendLine("</table>");
        return sb.ToString();
    }

    private static string RenderTimelineBars(List<JobGroup> groups, double totalSeconds, double minTotalSeconds = 0)
    {
        if (groups.Count == 0 || totalSeconds <= 0)
        {
            return "No job data to display.\n";
        }

        var phaseJobs = new Dictionary<string, List<JobInfo>>();
        foreach (var g in groups)
        {
            var phase = ClassifyPhase(g.Name);
            foreach (var j in g.Jobs)
            {
                if (j.CompletedAt >= minTotalSeconds && IsSignificantJob(j))
                {
                    if (!phaseJobs.TryGetValue(phase, out var list))
                    {
                        list = [];
                        phaseJobs[phase] = list;
                    }
                    list.Add(j);
                }
            }
        }

        foreach (var list in phaseJobs.Values)
        {
            list.Sort((a, b) => b.CompletedAt.CompareTo(a.CompletedAt));
        }

        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Job</th><th>Total</th><th>Deps</th><th>Queue</th><th>Run</th></tr>");

        foreach (var phase in s_phaseOrder)
        {
            if (!phaseJobs.TryGetValue(phase, out var jobsInPhase) || jobsInPhase.Count == 0)
            {
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td colspan=\"5\"><b>📁 {phase}</b> ({jobsInPhase.Count} jobs)</td></tr>");

            foreach (var j in jobsInPhase)
            {
                var name = E(ShortName(GroupBaseName(j.Name)));
                var runner = j.Labels.Count > 0 ? j.Labels[0] : "";
                var icon = s_statusIcon.GetValueOrDefault(j.Conclusion, "");
                var ri = runner.Length > 0 ? GetRunnerIcon(runner) + " " : "";

                sb.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{icon} {ri}<code>{name}</code></td>"
                    + $"<td><b>{FormatDuration(j.CompletedAt)}</b></td>"
                    + $"<td>{FormatDuration(j.CreatedAt)}</td>"
                    + $"<td>{FormatDuration(j.QueueTime)}</td>"
                    + $"<td>{FormatDuration(j.RunTime)}</td></tr>");
            }
        }

        sb.AppendLine("</table>");
        return sb.ToString();
    }

    public static string GenerateSummary(JsonElement runInfo, List<JsonElement> jobs, double minTotalMinutes = 0)
    {
        var allCreated = jobs
            .Select(j => ParseTs(j, "created_at"))
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        var allCompleted = jobs
            .Select(j => ParseTs(j, "completed_at"))
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        if (allCreated.Count == 0)
        {
            return "⚠️ No job data available for timeline.\n";
        }

        // Use run_started_at as the baseline when available and earlier than the first
        // job's created_at so that initial workflow queue/wait time is captured.
        var t0 = allCreated.Min();
        var runStartedAt = ParseTs(runInfo, "run_started_at");
        if (runStartedAt is not null && runStartedAt.Value < t0)
        {
            t0 = runStartedAt.Value;
        }

        var totalSeconds = 0.0;
        if (allCompleted.Count > 0)
        {
            // Only count jobs that actually ran in this attempt
            var actualCompleted = jobs
                .Where(j =>
                {
                    var completed = ParseTs(j, "completed_at");
                    var started = ParseTs(j, "started_at");
                    var created = ParseTs(j, "created_at");
                    return completed is not null && started is not null && created is not null && started >= created;
                })
                .Select(j => ParseTs(j, "completed_at")!.Value)
                .ToList();

            if (actualCompleted.Count > 0)
            {
                totalSeconds = (actualCompleted.Max() - t0).TotalSeconds;
            }
        }

        // For first attempts, fall back to run-level timestamps if job window is smaller.
        var runAttempt = GetInt(runInfo, "run_attempt", 1);
        if (runAttempt <= 1)
        {
            var runUpdated = ParseTs(runInfo, "updated_at");
            if (runStartedAt is not null && runUpdated is not null)
            {
                totalSeconds = Math.Max(totalSeconds, (runUpdated.Value - runStartedAt.Value).TotalSeconds);
            }
        }

        var groups = BuildJobGroups(jobs, t0);

        var conclusion = GetString(runInfo, "conclusion");
        if (string.IsNullOrEmpty(conclusion))
        {
            conclusion = GetString(runInfo, "status");
        }
        var conclusionIcon = s_statusIcon.GetValueOrDefault(conclusion, "");

        var sb = new StringBuilder();

        // Header
        sb.AppendLine("<h2>⏱️ CI Timeline</h2>");
        var conclusionLabel = string.IsNullOrEmpty(conclusion) ? "in progress" : conclusion;
        var attemptNote = runAttempt > 1 ? $" (re-run attempt #{runAttempt})" : "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"<p>{conclusionIcon} <b>{E(conclusionLabel)}</b> — Total: <b>{FormatDuration(totalSeconds)}</b>{attemptNote}</p>");

        // Summary table
        sb.Append(RenderSummaryTable(groups, totalSeconds));

        var minTotalSecs = minTotalMinutes * 60;

        // Critical path
        var cpThreshold = Math.Max(minTotalSecs, 300);
        sb.AppendLine("<details open>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<summary><b>🐢 Critical path</b> — jobs with longest total pipeline time (deps + queue + run, showing total &gt; {cpThreshold / 60:0}min)</summary>");
        sb.AppendLine();
        sb.Append(RenderCriticalPath(groups, minTotalSecs));
        sb.AppendLine();
        sb.AppendLine("</details>");

        // Queue wait hotspots
        var hotspots = RenderHotspots(groups);
        if (!string.IsNullOrEmpty(hotspots))
        {
            sb.AppendLine("<details open>");
            sb.AppendLine("<summary><b>⏳ Queue hotspots</b> — longest queue waits</summary>");
            sb.AppendLine();
            sb.Append(hotspots);
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        // Full timeline chart
        sb.AppendLine("<details>");
        var label = "<b>📊 Full timeline</b> — all jobs by phase";
        if (minTotalMinutes > 0)
        {
            label += $" (total &gt; {minTotalMinutes:0}min)";
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"<summary>{label}</summary>");
        sb.AppendLine();
        sb.Append(RenderTimelineBars(groups, totalSeconds, minTotalSecs));
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        return sb.ToString();
    }

    public static string WrapHtml(string body)
    {
        return """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>CI Timeline</title>
            <style>
            body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                   max-width: 1200px; margin: 0 auto; padding: 20px; background: #fff;
                   color: #24292f; line-height: 1.5; }
            table { border-collapse: collapse; margin: 16px 0; }
            th, td { border: 1px solid #d0d7de; padding: 6px 13px; text-align: left; }
            th { background: #f6f8fa; font-weight: 600; }
            code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
                    font-size: 12px; background: #f6f8fa; padding: 2px 5px; border-radius: 4px; }
            details { margin: 8px 0; }
            summary { cursor: pointer; font-weight: 600; padding: 8px 0; }
            h2 { border-bottom: 1px solid #d0d7de; padding-bottom: 8px; }
            h3 { margin-top: 24px; }
            a { color: #0969da; }
            </style></head><body>
            """ + body + """

            </body></html>
            """;
    }
}
