// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Guards on the CI wiring that surrounds the SelectTests engine but lives in YAML rather than C#:
/// the <c>run-full-ci</c> label kill switch (computed in <c>.github/workflows/tests.yml</c>, consumed by
/// <c>.github/actions/select-tests/action.yml</c>) and the selection-comment posting in
/// <c>tests.yml</c>. Neither is exercised by the CLI tests, yet both are easy to silently regress
/// (loosen the kill switch, or revert the comment to update-in-place), so they are pinned here.
/// </summary>
public sealed class SelectTestsWorkflowTests
{
    // The kill switch is a maintainer-only PR label, not a PR-body token. The action must consume it
    // as a plain boolean (forceAll) and must NOT re-introduce body scanning (a grep over an untrusted
    // PR description -- the injection surface this design deliberately removed).
    [Fact]
    public void SelectTestsActionGatesForceAllOnBooleanInputNotPrBody()
    {
        var action = File.ReadAllText(SelectTestsActionPath);

        Assert.Contains("forceAll:", action);
        Assert.Contains("FORCE_ALL: ${{ inputs.forceAll }}", action);
        Assert.Contains("[ \"$FORCE_ALL\" = \"true\" ]", action);

        // No body-scanning kill switch: the PR body must not flow into the action at all.
        Assert.DoesNotContain("prBody", action);
        Assert.DoesNotContain("PR_BODY", action);
        Assert.DoesNotContain("full ci", action);
    }

    // tests.yml must compute forceAll from the presence of the 'run-full-ci' label on the PR, read from
    // the event-payload snapshot. If the label name or the contains() expression drifts, the kill
    // switch silently stops working (it would just never force-all), so pin the exact wiring.
    [Fact]
    public void TestsWorkflowComputesForceAllFromFullCiLabel()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        Assert.Contains(
            "forceAll: ${{ contains(github.event.pull_request.labels.*.name, 'run-full-ci') }}",
            testsYml);
    }

    // The comment_selection job posts one comment per pushed commit (createComment for a new commit,
    // updateComment for a re-run of the same commit) and collapses superseded comments with
    // minimizeComment -- it must never delete. This guard fails if deletion is introduced or the
    // head-commit link (also the idempotency key) is dropped.
    [Fact]
    public void CommentSelectionJobIsIdempotentPerCommitAndCollapsesSuperseded()
    {
        var job = ExtractCommentSelectionJob();

        Assert.Contains("github.rest.issues.createComment", job);
        Assert.Contains("github.rest.issues.updateComment", job);
        Assert.Contains("minimizeComment", job);
        Assert.DoesNotContain("deleteComment", job);

        // Minimization is gated on this run being the PR's live head (a stale re-run must not collapse
        // a newer commit's comment), resolved via pulls.get.
        Assert.Contains("github.rest.pulls.get", job);

        // The marker is read back to find prior comments; the head SHA links the commit and keys
        // create-vs-update.
        Assert.Contains("<!-- select-tests-comment -->", job);
        Assert.Contains("pull_request?.head?.sha", job);
        Assert.Contains("/commit/", job);
    }

    private static string SelectTestsActionPath
        => Path.Combine(RepoRoot.Path, ".github", "actions", "select-tests", "action.yml");

    private static string TestsWorkflowPath
        => Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml");

    private static string ExtractCommentSelectionJob()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        var start = testsYml.IndexOf("comment_selection:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a comment_selection job in {TestsWorkflowPath}.");

        // Bound the slice at the next top-level job so assertions can't match other jobs' scripts.
        var end = testsYml.IndexOf("build_packages:", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected the comment_selection job to precede build_packages in {TestsWorkflowPath}.");

        return testsYml[start..end];
    }
}
