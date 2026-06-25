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

    [Fact]
    public void BuildUrl_BugReport_UsesBugTemplateAndExpectedQueryKeys()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Bug,
            Title: "Crash",
            MainText: "Repro",
            AspireDoctorOutput: "OK",
            AdditionalContext: "ctx"));

        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/new?template=10_bug_report.yml&title=Crash&description=Repro&aspire-doctor-output=OK&additional-context=ctx",
            url);
    }

    [Fact]
    public void BuildUrl_FeatureRequest_UsesFeatureTemplateAndExpectedQueryKeys()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Idea,
            Title: "Idea",
            MainText: "Soln",
            AdditionalContext: "ctx"));

        // Feature requests carry no doctor output; the main text maps to the `solution` field.
        Assert.Equal(
            "https://github.com/microsoft/aspire/issues/new?template=20_feature-request.yml&title=Idea&solution=Soln&additional-context=ctx",
            url);
    }

    [Fact]
    public void BuildUrl_GeneralFeedback_UsesBlankIssueWithTitleAndBody()
    {
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.General,
            Title: "Hello",
            MainText: "World"));

        // General feedback opens a blank issue (no template) with the main text in the body. The body
        // is built with AppendLine, so a platform-specific trailing newline follows "World"; assert the
        // stable prefix to stay OS-agnostic while still proving there is no template key and the order.
        Assert.StartsWith(
            "https://github.com/microsoft/aspire/issues/new?title=Hello&body=World",
            url,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUrl_LargeBugDescription_TruncatesDescriptionButKeepsTitle()
    {
        // A user can paste an arbitrarily large description. With no doctor output or additional context
        // to trim first, the description itself must be truncated so the URL stays valid.
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Bug,
            Title: "Keep me",
            MainText: new string('B', 40_000)));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        Assert.Contains("Truncated", url, StringComparison.Ordinal);
        // The title is the last-resort trim target, so it is preserved in full.
        Assert.Contains("title=Keep%20me", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUrl_LargeFeatureSolution_TruncatesSolutionButKeepsTitle()
    {
        // The feature-request solution is the largest free-text field once additional context is absent,
        // so it must be truncatable to keep the URL under GitHub's limit.
        var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
            Kind: FeedbackIssueKind.Idea,
            Title: "Keep me",
            MainText: new string('S', 40_000)));

        Assert.True(url.Length <= MaxUrlLength, $"URL length {url.Length} exceeded {MaxUrlLength}.");
        Assert.Contains("Truncated", url, StringComparison.Ordinal);
        Assert.Contains("title=Keep%20me", url, StringComparison.Ordinal);
    }
}
