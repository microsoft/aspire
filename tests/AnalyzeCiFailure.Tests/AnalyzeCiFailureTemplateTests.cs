// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure;
using System.Text.Json;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies published Markdown layouts and literal-safe template substitution.</summary>
public sealed class AnalyzeCiFailureTemplateTests
{
    [Theory]
    [InlineData("TransientInfrastructure")]
    [InlineData("FlakyTest")]
    [InlineData("CodeIssue")]
    [InlineData("Mixed")]
    public async Task VerdictCommentsRetainTheirPublishedLayout(string verdict)
    {
        var id = JsonSerializer.SerializeToElement(value: 123);
        var classification = verdict switch
        {
            "TransientInfrastructure" => JobClassification.TransientInfrastructure,
            "CodeIssue" => JobClassification.CodeIssue,
            _ => JobClassification.FlakyTest,
        };
        var report = new AnalysisReport
        {
            Verdict = Enum.Parse<AnalysisVerdict>(verdict),
            Jobs = [new AnalysisJob { Id = id, Classification = classification, Reason = "Classified failure" }],
        };
        var body = PrCommentRenderer.Render(
            report,
            [new WorkflowJob { Id = id, Name = "Tests" }],
            "https://github.com/microsoft/aspire/actions/runs/123");

        await Verifier.Verify(body, "md").UseDirectory("Snapshots").UseParameters(verdict);
    }

    [Fact]
    public void SubstitutedTextIsNotInterpretedAsAnotherPlaceholder()
    {
        var result = MarkdownTemplate.Render(
            "CommentTransientInfrastructure",
            ("FAILED_JOBS", "diagnostic ##RUN_URL## $1 ${name}"),
            ("RUN_URL", "https://example.invalid/run"));

        Assert.Contains("diagnostic ##RUN_URL## $1 ${name}", result, StringComparison.Ordinal);
        Assert.Contains("[workflow run page](https://example.invalid/run)", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-template")]
    [InlineData("missing-value")]
    [InlineData("unused-value")]
    public void InvalidTemplateBindingsFailExplicitly(string scenario)
    {
        Assert.Throws<InvalidOperationException>(() => scenario switch
        {
            "missing-template" => MarkdownTemplate.Render("NotAnEmbeddedTemplate"),
            "missing-value" => MarkdownTemplate.Render("CommentCodeIssue"),
            "unused-value" => MarkdownTemplate.Render("CommentCodeIssue", ("FAILED_JOBS", ""), ("UNUSED", "")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        });
    }
}
