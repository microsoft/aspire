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

    // The selector diffs head against the merge-base of base..head, so it must be handed the PR's REAL
    // head (pull_request.head.sha). github.sha is the synthetic refs/pull/N/merge commit, regenerated
    // asynchronously as the base advances; feeding it lets base-branch churn leak into the diff and
    // over-select (microsoft/aspire#18377). Pin the real-head wiring and forbid a revert to github.sha.
    [Fact]
    public void TestsWorkflowPassesPrHeadShaNotMergeRefToSelector()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        Assert.Contains("headSha: ${{ github.event.pull_request.head.sha }}", testsYml);
        Assert.DoesNotContain("headSha: ${{ github.sha }}", testsYml);
    }

    // On a PR the action diffs from the merge-base of base..head, which the shallow CI checkout can't
    // see until it is deepened. The step must deepen BOTH endpoints until `git merge-base` resolves and,
    // if it never does within a bounded number of fetches, FAIL rather than fall back to --force-all -- a
    // missing merge-base would otherwise crash the selector or, worse, silently under-select. Pin the
    // loop, its termination bound, and the hard failure so a "simplification" can't turn the failure into
    // a run-all or an unbounded fetch.
    [Fact]
    public void SelectTestsActionDeepensUntilMergeBaseReachableThenFailsLoud()
    {
        var action = File.ReadAllText(SelectTestsActionPath);

        // The deepen loop gates on the two endpoints' merge-base actually resolving locally.
        Assert.Contains("until git merge-base \"$PR_BASE_SHA\" \"$HEAD_SHA\"", action);
        // Each iteration re-fetches both endpoints at a growing depth.
        Assert.Contains("git fetch --no-tags --depth=\"$depth\" origin \"$PR_BASE_SHA\" \"$HEAD_SHA\"", action);
        // Bounded so a pathological history can't fetch forever.
        Assert.Contains("-ge 4096", action);
        // An unresolved merge-base is a hard failure, NOT a --force-all fallback.
        Assert.Contains("cannot compute the PR diff.", action);
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
