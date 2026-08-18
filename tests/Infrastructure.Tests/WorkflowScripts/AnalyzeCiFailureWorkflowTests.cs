// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureWorkflowTests
{
    private static readonly string s_workflow = File.ReadAllText(
        Path.Combine(RepoRoot.Path, ".github", "workflows", "analyze-ci-failure.md"));

    [Fact]
    public void RunScopeComesFromAnalyzedRunMetadata()
    {
        Assert.Contains("RUN_EVENT=$(jq -r '.event // \"\"' ci-failure-data/run.json)", s_workflow, StringComparison.Ordinal);
        Assert.Contains("case \"${RUN_EVENT}:${HEAD_BRANCH}\" in", s_workflow, StringComparison.Ordinal);
        Assert.Contains("push:main)", s_workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:*|pull_request_target:*)", s_workflow, StringComparison.Ordinal);
        Assert.Contains("RUN_SCOPE=\"main\"", s_workflow, StringComparison.Ordinal);
        Assert.Contains("RUN_SCOPE=\"pull-request\"", s_workflow, StringComparison.Ordinal);
        Assert.Contains("Unsupported run scope: event=${RUN_EVENT}, branch=${HEAD_BRANCH}", s_workflow, StringComparison.Ordinal);
        Assert.Contains("run_scope: $run_scope", s_workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void MainRunContextTreatsTriggeringMergeAsNonCausal()
    {
        Assert.Contains("last-successful-main-run.json", s_workflow, StringComparison.Ordinal);
        Assert.Contains("candidate-merges.json", s_workflow, StringComparison.Ordinal);
        Assert.Contains(
            "Triggering merge PR (context only, not necessarily causal)",
            s_workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "consider the complete candidate merge range since the last successful main run",
            s_workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainRepositoryBreakageUsesDedicatedIssueAndNeverPrComment()
    {
        Assert.Contains(
            "Deterministic compilation, test, API compatibility, lint, or formatting failures are `main-repository-breakage`",
            s_workflow,
            StringComparison.Ordinal);
        Assert.Contains("CAUSE_TYPE\" = \"main-repository-breakage", s_workflow, StringComparison.Ordinal);
        Assert.Contains("LABELS=\"ci-failure-cause,main-ci-break\"", s_workflow, StringComparison.Ordinal);
        Assert.Contains("ISSUE_TITLE=$(jq -r '\"[Main CI Failure] \" + .title'", s_workflow, StringComparison.Ordinal);
        Assert.Contains("Main run analysis is reported through cause issues, not PR comments.", s_workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherValidatesAgentResultAgainstTrustedScope()
    {
        Assert.Contains("RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"", s_workflow, StringComparison.Ordinal);
        Assert.Contains("ANALYSIS_RUN_SCOPE=$(jq -r '.run_scope' \"$ANALYSIS_FILE\")", s_workflow, StringComparison.Ordinal);
        Assert.Contains("Analysis result does not match trusted run context", s_workflow, StringComparison.Ordinal);
        Assert.Contains("Main run analysis must not identify a subject PR or use the PR code-issue verdict", s_workflow, StringComparison.Ordinal);
    }
}
