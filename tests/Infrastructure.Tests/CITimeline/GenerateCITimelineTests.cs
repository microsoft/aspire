// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Infrastructure.Tests.CITimeline;

public partial class GenerateCITimelineTests
{
    private static string GetTestDataPath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "CITimeline", "TestData", filename);

    private static (JsonElement RunInfo, List<JsonElement> Jobs) LoadTestData(string filename) =>
        GitHubApi.LoadJsonData(GetTestDataPath(filename));

    [Fact]
    public void GenerateSummary_BasicRun_UsesRunWindowForWallTime()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Equal("45m00s", GetMetricValue(summary, "Total wall time"));
    }

    [Fact]
    public void GenerateSummary_BasicRun_RanksOnlyNonResultJobsOnCriticalPath()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);
        var section = GetDetailsSection(summary, "<summary><b>🐢 Critical path</b>");

        Assert.Equal(
        [
            "1 | ❌ 🪟 Aspire.Hosting.Tests | 42m00s | 18m00s | 2m00s | 22m00s",
            "2 | ✅ 🐧⚡ Aspire.Hosting.Tests | 40m00s | 15m00s | 1m00s | 24m00s",
            "3 | ✅ 🪟 Build | 18m00s | 2m00s | 2m00s | 14m00s",
            "4 | ✅ 🐧 Build | 15m00s | 2m00s | 1m00s | 12m00s",
        ],
            GetJobRows(section));
    }

    [Fact]
    public void GenerateSummary_BasicRun_RanksQueueHotspots()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);
        var section = GetDetailsSection(summary, "<summary><b>⏳ Queue hotspots</b>");

        Assert.Equal(
        [
            "⏳ 🪟 Build | 2m00s",
            "⏳ 🪟 Aspire.Hosting.Tests | 2m00s",
            "⏳ 🐧 Build | 1m00s",
            "⏳ 🐧⚡ Aspire.Hosting.Tests | 1m00s",
        ],
            GetRows(section).Select(row => string.Join(" | ", row)));
    }

    [Fact]
    public void GenerateSummary_MinTotalMinutes_UsesStrictBoundary()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs, minTotalMinutes: 15);
        var criticalPath = GetDetailsSection(summary, "<summary><b>🐢 Critical path</b>");
        var fullTimeline = GetDetailsSection(summary, "<summary><b>📊 Full timeline</b>");

        Assert.Equal(
        [
            "1 | ❌ 🪟 Aspire.Hosting.Tests | 42m00s | 18m00s | 2m00s | 22m00s",
            "2 | ✅ 🐧⚡ Aspire.Hosting.Tests | 40m00s | 15m00s | 1m00s | 24m00s",
            "3 | ✅ 🪟 Build | 18m00s | 2m00s | 2m00s | 14m00s",
        ],
            GetJobRows(criticalPath));
        Assert.Equal(
        [
            "✅ 🪟 Build | 18m00s | 2m00s | 2m00s | 14m00s",
            "❌ 🪟 Aspire.Hosting.Tests | 42m00s | 18m00s | 2m00s | 22m00s",
            "✅ 🐧⚡ Aspire.Hosting.Tests | 40m00s | 15m00s | 1m00s | 24m00s",
        ],
            GetJobRows(fullTimeline));
    }

    [Fact]
    public void GenerateSummary_MinTotalMinutes_ExcludesAllJobsAtHighThreshold()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs, minTotalMinutes: 999);

        Assert.Empty(GetJobRows(GetDetailsSection(summary, "<summary><b>🐢 Critical path</b>")));
        Assert.Empty(GetJobRows(GetDetailsSection(summary, "<summary><b>📊 Full timeline</b>")));
    }

    [Fact]
    public void GenerateSummary_EncodesUntrustedRunConclusionJobNameAndRunnerLabel()
    {
        using var runDocument = JsonDocument.Parse("""
            {
              "run_started_at": "2026-01-15T10:00:00Z",
              "updated_at": "2026-01-15T10:10:00Z",
              "conclusion": "success <run>&\"",
              "status": "completed",
              "run_attempt": 2
            }
            """);
        using var jobsDocument = JsonDocument.Parse("""
            [
              {
                "name": "Build <job> & value (ubuntu-latest)",
                "status": "completed",
                "conclusion": "success",
                "created_at": "2026-01-15T10:00:00Z",
                "started_at": "2026-01-15T10:01:00Z",
                "completed_at": "2026-01-15T10:10:00Z",
                "runner_name": "runner <runner>&\"",
                "html_url": "https://github.com/test/run/1",
                "labels": ["ubuntu <runner>&\""]
              }
            ]
            """);
        var jobs = jobsDocument.RootElement.EnumerateArray().Select(job => job.Clone()).ToList();
        var summary = TimelineRenderer.GenerateSummary(runDocument.RootElement, jobs);

        Assert.Contains("<b>success &lt;run&gt;&amp;&quot;</b>", summary);
        Assert.Contains("<code>Build &lt;job&gt; &amp; value</code>", summary);
        Assert.Contains("<code>ubuntu &lt;runner&gt;&amp;&quot;</code>: 1", summary);
    }

    [Fact]
    public void GenerateSummary_NullConclusion_ShowsInProgress()
    {
        var (runInfo, jobs) = LoadTestData("null-conclusion-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("in_progress", summary);
    }

    [Fact]
    public void GenerateSummary_EmptyJobs_ShowsWarning()
    {
        var (runInfo, jobs) = LoadTestData("empty-jobs.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("No job data available", summary);
    }

    [Fact]
    public void GenerateSummary_RerunAttempt_ExcludesPriorAttemptJobsAndUsesAttemptWindow()
    {
        var (runInfo, jobs) = LoadTestData("rerun-attempt.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);
        var fullTimeline = GetDetailsSection(summary, "<summary><b>📊 Full timeline</b>");

        Assert.Contains("re-run attempt #2", summary);
        Assert.Equal("1 (1 unique)", GetMetricValue(summary, "Jobs"));
        Assert.Equal("15m00s", GetMetricValue(summary, "Total wall time"));
        Assert.Equal(
        [
            "✅ 🐧 Build | 15m00s | 0s | 1m00s | 14m00s",
        ],
            GetJobRows(fullTimeline));
    }

    [Fact]
    public void LoadJsonData_BasicRun_ParsesRunInfo()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");

        Assert.Equal("success", runInfo.GetProperty("conclusion").GetString());
        Assert.True(jobs.Count > 0);
    }

    [Fact]
    public void LoadJsonData_BasicRun_ParsesAllJobs()
    {
        var (_, jobs) = LoadTestData("basic-run.json");

        Assert.Equal(6, jobs.Count);
    }

    private static string GetDetailsSection(string summary, string summaryMarker)
    {
        var summaryStart = summary.IndexOf(summaryMarker, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0, $"Could not find details section summary '{summaryMarker}'.");

        var detailsStart = summary.LastIndexOf("<details", summaryStart, StringComparison.Ordinal);
        Assert.True(detailsStart >= 0, $"Could not find opening details boundary for '{summaryMarker}'.");

        var detailsEnd = summary.IndexOf("</details>", summaryStart, StringComparison.Ordinal);
        Assert.True(detailsEnd >= 0, $"Could not find closing details boundary for '{summaryMarker}'.");

        return summary[detailsStart..(detailsEnd + "</details>".Length)];
    }

    private static string GetMetricValue(string summary, string metric)
    {
        var row = Assert.Single(GetRows(summary), row => row is [var name, _] && name == metric);
        return row[1];
    }

    private static string[] GetJobRows(string section) =>
        [.. GetRows(section)
            .Where(row => row.Count > 2)
            .Select(row => string.Join(" | ", row))];

    private static List<List<string>> GetRows(string html) =>
        [.. html.Split('\n')
            .Where(line => line.StartsWith("<tr><td>", StringComparison.Ordinal))
            .Select(line => CellRegex().Matches(line)
                .Select(match => WebUtility.HtmlDecode(TagRegex().Replace(match.Groups[1].Value, string.Empty)))
                .ToList())];

    [GeneratedRegex(@"<td(?:\s+[^>]*)?>(.*?)</td>")]
    private static partial Regex CellRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}
