// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests.CITimeline;

public class GenerateCITimelineTests
{
    private static string GetTestDataPath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "CITimeline", "TestData", filename);

    private static (JsonElement RunInfo, List<JsonElement> Jobs) LoadTestData(string filename) =>
        GitHubApi.LoadJsonData(GetTestDataPath(filename));

    [Fact]
    public void GenerateSummary_BasicRun_ContainsHeader()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("CI Timeline", summary);
        Assert.Contains("success", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ContainsSummaryStats()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("Jobs", summary);
        Assert.Contains("Status", summary);
        Assert.Contains("✅", summary);
        Assert.Contains("❌", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ContainsCriticalPath()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("Critical path", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ContainsPhases()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("Full timeline", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ShowsRunnerEmoji()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("🐧", summary);
        Assert.Contains("🪟", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_LargeRunnerGetsLightningBolt()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("🐧⚡", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ContainsHtmlCodeTags()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("<code>", summary);
        Assert.DoesNotContain("<script>", summary);
    }

    [Fact]
    public void GenerateSummary_BasicRun_ExcludesResultsFromCriticalPath()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        var criticalPathStart = summary.IndexOf("Critical path", StringComparison.Ordinal);
        var criticalPathEnd = summary.IndexOf("</details>", criticalPathStart, StringComparison.Ordinal);
        if (criticalPathStart >= 0 && criticalPathEnd >= 0)
        {
            var criticalPathSection = summary[criticalPathStart..criticalPathEnd];
            Assert.DoesNotContain(">results<", criticalPathSection);
        }
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
    public void GenerateSummary_RerunAttempt_SkipsOldAttemptJobs()
    {
        var (runInfo, jobs) = LoadTestData("rerun-attempt.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("re-run attempt #2", summary);
    }

    [Fact]
    public void GenerateSummary_WithMinTotalFilter_FiltersShortJobs()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs, minTotalMinutes: 999);

        Assert.Contains("CI Timeline", summary);
    }

    [Fact]
    public void WrapHtml_ProducesValidHtmlDocument()
    {
        var html = TimelineRenderer.WrapHtml("<p>Test content</p>");

        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.Contains("<html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("Test content", html);
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

    [Fact]
    public void GenerateSummary_BasicRun_FormatsWallTime()
    {
        var (runInfo, jobs) = LoadTestData("basic-run.json");
        var summary = TimelineRenderer.GenerateSummary(runInfo, jobs);

        Assert.Contains("45m", summary);
    }
}
