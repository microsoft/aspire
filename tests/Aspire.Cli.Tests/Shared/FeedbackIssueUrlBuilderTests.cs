// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Tests.Shared;

public sealed class FeedbackIssueUrlBuilderTests
{
    // GitHub rejects requests whose URL exceeds roughly 8 KB; the builder caps generated URLs at 8000.
    private const int MaxUrlLength = 8000;

    [Fact]
    public void BuildUrl_SmallBugReport_IncludesFullDoctorOutputAndContext()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Bug,
            Title: "Something broke",
            MainText: "Steps to reproduce",
            AspireDoctorOutput: """{"overallStatus":"Healthy"}""",
            AdditionalContext: "- Posted from: CLI"));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        Assert.Contains("template=10_bug_report.yml", url, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("""{"overallStatus":"Healthy"}"""), url, StringComparison.Ordinal);
        Assert.DoesNotContain("Truncated", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUrl_LargeDoctorOutput_TruncatesDoctorOutputButKeepsContext()
    {
        // A realistic large `aspire doctor` payload that would push the URL well past GitHub's limit.
        var largeDoctorOutput = new string('D', 50_000);

        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Bug,
            Title: "Big report",
            MainText: "Repro steps",
            AspireDoctorOutput: largeDoctorOutput,
            AdditionalContext: "- Posted from: CLI UNIQUECONTEXTTOKEN"));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        // The marker proves the doctor output was trimmed rather than silently dropped.
        Assert.Contains("Truncated", url, StringComparison.Ordinal);
        // The smaller additional-context block is preserved (doctor output is trimmed first).
        Assert.Contains("UNIQUECONTEXTTOKEN", url, StringComparison.Ordinal);
        // The human-entered title/description are never trimmed.
        Assert.Contains("title=Big%20report", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUrl_LargeDoctorOutputAndContext_StaysUnderLimit()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Bug,
            Title: "Overflowing report",
            MainText: "Repro steps",
            AspireDoctorOutput: new string('D', 40_000),
            AdditionalContext: new string('C', 40_000)));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
    }

    [Fact]
    public void BuildUrl_LargeFeatureRequestContext_TruncatesAdditionalContext()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Idea,
            Title: "Great idea",
            MainText: "The solution",
            AdditionalContext: new string('C', 40_000)));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        Assert.Contains("template=20_feature-request.yml", url, StringComparison.Ordinal);
        Assert.Contains("Truncated", url, StringComparison.Ordinal);
        Assert.Contains("solution=The%20solution", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUrl_LargeGeneralFeedbackBody_TruncatesBody()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.General,
            Title: "General feedback",
            MainText: new string('B', 40_000),
            AdditionalContext: "- Posted from: CLI"));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        Assert.Contains("title=General%20feedback", url, StringComparison.Ordinal);
        Assert.Contains("Truncated", url, StringComparison.Ordinal);
    }
}
