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
    // over-select (microsoft/aspire#18377). Pin the real-head wiring -- the caller's explicit input AND the
    // action's own default -- and forbid a revert to github.sha in either, so a future caller relying on
    // the default can't silently reintroduce the bug.
    [Fact]
    public void TestsWorkflowPassesPrHeadShaNotMergeRefToSelector()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        Assert.Contains("headSha: ${{ github.event.pull_request.head.sha }}", testsYml);
        Assert.DoesNotContain("headSha: ${{ github.sha }}", testsYml);

        // The action's headSha default must also be the real head, not the synthetic merge ref.
        var action = File.ReadAllText(SelectTestsActionPath);
        Assert.Contains("default: ${{ github.event.pull_request.head.sha }}", action);
        Assert.DoesNotContain("default: ${{ github.sha }}", action);
    }

    // On a PR the action diffs from the merge-base of base..head, which the shallow CI checkout can't
    // see until it is deepened. The step must deepen BOTH endpoints until `git merge-base` resolves and,
    // if it never does within a bounded number of fetches, degrade to --force-all (run ALL) rather than
    // fail the PR -- a missing merge-base must not block PRs while the wiring is fixed. Pin the loop, its
    // termination bound, the warning, and the --force-all fallback so a regression can't turn it back into
    // a hard failure or an unbounded fetch.
    [Fact]
    public void SelectTestsActionDeepensUntilMergeBaseReachableThenFallsBackToAll()
    {
        var action = File.ReadAllText(SelectTestsActionPath);

        // The deepen loop gates on the two endpoints' merge-base actually resolving locally.
        Assert.Contains("until git merge-base \"$PR_BASE_SHA\" \"$HEAD_SHA\"", action);
        // Each iteration re-fetches both endpoints at a growing depth.
        Assert.Contains("git fetch --no-tags --depth=\"$depth\" origin \"$PR_BASE_SHA\" \"$HEAD_SHA\"", action);
        // Bounded so a pathological history can't fetch forever.
        Assert.Contains("-ge 4096", action);
        // An unresolved merge-base warns and degrades to run-all -- it must NOT be a hard ::error::/exit.
        Assert.Contains("::warning::Could not find a merge-base", action);
        Assert.DoesNotContain("::error::Could not find a merge-base", action);

        // The unresolved-merge-base path must DEGRADE to run-all, never hard-fail the PR. A bare
        // Contains("args+=(--force-all)") is non-discriminating: that string also appears in the
        // kill-switch and non-PR branches, so it would still pass if THIS branch were turned into an
        // `exit 1`. Scope the assertions to the merge-base resolution + fallback region -- past the
        // base-fetch guards that legitimately `exit 1` -- so a regression to a hard failure is caught.
        var resolveStart = action.IndexOf("merge_base_found=true", StringComparison.Ordinal);
        Assert.True(resolveStart >= 0, "merge-base resolution region not found in action.yml");
        // The closing `fi` of the `if [ "$merge_base_found" = "true" ]` gate, matched at its 10-space
        // indent so the loop's inner `fi` (12 spaces) and the outer branch `fi` (8 spaces) don't match.
        var resolveEnd = action.IndexOf("\n          fi", resolveStart, StringComparison.Ordinal);
        Assert.True(resolveEnd > resolveStart, "merge_base_found gate is not closed as expected");
        var mergeBaseRegion = action[resolveStart..resolveEnd];

        // Give-up path breaks out of the loop rather than exiting ...
        Assert.Contains("break", mergeBaseRegion);
        // ... and the gate degrades to run-all, recording the reason for the audit summary.
        Assert.Contains("--force-all --force-all-reason", mergeBaseRegion);
        // Nothing in the resolution or fallback may hard-fail the PR.
        Assert.DoesNotContain("exit", mergeBaseRegion);
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
