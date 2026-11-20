// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [InlineData("Unresolved")]
    public async Task VerdictCommentsRetainTheirPublishedLayout(string verdict)
    {
        var id = JsonSerializer.SerializeToElement(value: 123);
        var classification = verdict switch
        {
            "TransientInfrastructure" => JobClassification.TransientInfrastructure,
            "CodeIssue" => JobClassification.CodeIssue,
            "Unresolved" => JobClassification.Unresolved,
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
    public async Task MainIssueRetainsSanitizedDiagnosisAndErrorWithoutReplacingTrustedCommitTitle()
    {
        var cause = new JsonObject
        {
            ["id"] = "host-crash", ["type"] = "main-repository-breakage",
            ["title"] = "Test host crashed during resource startup @reviewers [details](https://example.invalid)",
            ["error_pattern"] = "Test host exited with exit code 3\nConnectionString=Server=private;Password=secret",
        };
        var draft = CauseIssueRenderer.RenderCanonicalIssue(
            cause,
            new RunContext { HeadSha = JsonSerializer.SerializeToElement("trusted-failure") },
            new PublicationEvidence(null, new WorkflowRun { HeadSha = JsonSerializer.SerializeToElement("trusted-success") },
                null, CandidateHistoryState.Unavailable, null),
            "https://github.com/microsoft/aspire/actions/runs/123", "main", "0", "Tests",
            "| 2026-11-04 | [123](https://github.com/microsoft/aspire/actions/runs/123) | ` Tests ` | main |");

        await Verifier.Verify(CiFailureJson.Serialize(draft), "json").UseDirectory("Snapshots");
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
