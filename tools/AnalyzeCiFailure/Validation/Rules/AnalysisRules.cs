// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Common;
using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Common.WorkflowCommands;
using static AnalyzeCiFailure.Validation.ValidationException;
using static AnalyzeCiFailure.Validation.ValidationPolicy;

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// The rules the agent's output must satisfy. Each rule throws <see cref="ValidationException"/>
/// with the message for the first violation it finds.
/// </summary>
internal static partial class ValidationRules
{
    /// <summary>The analysis must describe the run this workflow is analyzing.</summary>
    public static void AnalysisMustDescribeRun(AnalysisRunIdentity identity, TrustedRun run)
        => Require(identity.RunId == run.Id && identity.RunScope == run.Scope, ValidationMessages.RunContextMismatch);

    /// <summary>
    /// A main run has no subject PR. In a PR run, <c>pr</c> is required: an explicit null means the
    /// agent could not identify one; any other value must name the trusted subject PR.
    /// </summary>
    public static void SubjectPullRequestMustBeTrusted(AnalysisDocument analysis, TrustedRun run)
    {
        if (run.Scope == RunScopes.Main)
        {
            Require(analysis.Pr is null, ValidationMessages.MainRunHasPullRequest);
        }
        else if (run.Scope == RunScopes.PullRequest)
        {
            Require(
                analysis.HasPr && (analysis.Pr is null || (run.PrNumber is not null && analysis.Pr.Number == run.PrNumber)),
                ValidationMessages.UntrustedPullRequest);
        }
    }

    /// <summary>
    /// The analysis must have the expected shape and contain only publishable text. Returns the
    /// validated values the remaining rules work with.
    /// </summary>
    public static Analysis AnalysisMustBePublishable(AnalysisDocument analysis)
    {
        // Job IDs are matched against trusted jobs numerically, and cause IDs against file names.
        if (analysis.FailedJobs is not { } failedJobs || failedJobs.Any(job => job?.Id is null) ||
            analysis.Causes is not { } causeIds || causeIds.Any(id => id is null))
        {
            throw new ValidationException(ValidationMessages.InvalidIdArrays);
        }

        // The character checks block hidden or spoofed text.
        if (failedJobs.Any(job => job!.Reason is not null && !SafeText.IsSingleLine(job.Reason, MaxReasonLength)) ||
            analysis.FailedTests is not { } failedTests || !failedTests.All(IsPublishableFailedTest))
        {
            throw new ValidationException(ValidationMessages.UnsafeFailedTests);
        }

        return new Analysis(
            analysis.Verdict ?? "",
            causeIds.Select(id => id!).ToList(),
            failedJobs.Select(job => new FailedJob(job!.Id!.Value, job.Classification)).ToList(),
            failedTests.Select(test => new FailedTest(test!.Name!, test.Job!, test.Classification!)).ToList());
    }

    /// <summary>
    /// The reported (test, job) pairs must be exactly the failures in the trusted test results, with
    /// no omissions, inventions, or duplicates, and each job must be a real failed job.
    /// </summary>
    public static void FailedTestsMustMatchEvidence(Analysis analysis, TestEvidence evidence, List<TrustedJob> trustedJobs)
    {
        switch (evidence.State)
        {
            case TestEvidenceStates.Unavailable:
                // Without results, flaky-test claims cannot be checked, so nothing is published.
                throw new ValidationException("Trusted test evidence is unavailable");
            case TestEvidenceStates.NotApplicable:
                Require(analysis.FailedTests.Count == 0, ValidationMessages.TestEvidenceMismatch);
                return;
        }

        var reportedPairs = analysis.FailedTests.Select(test => (test.Name, test.Job)).ToList();
        var trustedPairs = evidence.Failures!.Select(test => (Name: test.Test, test.Job)).ToList();
        Require(
            reportedPairs.Distinct().Count() == reportedPairs.Count &&
            trustedPairs.Distinct().Count() == trustedPairs.Count &&
            reportedPairs.ToHashSet().SetEquals(trustedPairs) &&
            analysis.FailedTests.All(test => trustedJobs.Any(job => job.Name == test.Job)),
            ValidationMessages.TestEvidenceMismatch);
    }

    /// <summary>Each run scope allows a fixed set of verdicts.</summary>
    public static void VerdictMustBeAllowedForScope(string verdict, TrustedRun run)
        => Require(
            AllowedVerdicts.TryGetValue(run.Scope, out var allowedVerdicts) && allowedVerdicts.Contains(verdict),
            $"Verdict {Display(verdict)} is not permitted for run scope {Display(run.Scope)}");

    public static void CausesMustFitBudget(Analysis analysis, int causeFileCount)
        => Require(
            analysis.CauseIds.Count <= MaxCauseCount && causeFileCount <= MaxCauseCount,
            $"Analysis exceeds the {MaxCauseCount}-cause publication budget");

    /// <summary>The analysis must classify exactly the jobs that really failed, each with a classification its scope allows.</summary>
    public static void FailedJobsMustMatchTrustedJobs(Analysis analysis, List<TrustedJob> trustedJobs, TrustedRun run)
    {
        Require(
            analysis.FailedJobs.Count > 0 &&
            analysis.FailedJobs.All(job => job.Classification is { } classification && JobClassifications.All.Contains(classification)),
            ValidationMessages.UnclassifiedJobs);

        var analysisJobIds = analysis.FailedJobs.Select(job => job.Id).ToList();
        Require(
            analysisJobIds.Distinct().Count() == analysisJobIds.Count &&
            analysisJobIds.Order().SequenceEqual(trustedJobs.Select(job => job.Id).Order()),
            "Analysis failed-job IDs do not match the trusted failed jobs");

        var disallowedClassification = DisallowedJobClassification.GetValueOrDefault(run.Scope);
        Require(
            analysis.FailedJobs.All(job => job.Classification != disallowedClassification),
            $"Analysis contains a failed-job classification that is not permitted for run scope {Display(run.Scope)}");
    }

    /// <summary>The rendered PR comment must exist and fit the comment budget.</summary>
    public static void CommentMustFitBudget(int? commentBytes)
    {
        Require(commentBytes is not null, "Unable to render the PR comment during validation");
        Require(commentBytes <= MaxCommentBytes, $"Rendered PR comment exceeds the {MaxCommentBytes}-byte publication budget");
    }

    private static bool IsPublishableFailedTest(FailedTestEntry? test)
        => test is not null &&
            SafeText.IsSingleLine(test.Name, MaxTestNameLength) && test.Name!.Length > 0 &&
            SafeText.IsSingleLine(test.Job, MaxJobNameLength) && test.Job!.Length > 0 &&
            SafeText.IsMultiline(test.Error, MaxTestErrorLength) &&
            (test.StackTrace is null || SafeText.IsMultiline(test.StackTrace, MaxStackTraceLength)) &&
            (test.StandardOutput is null || SafeText.IsMultiline(test.StandardOutput, MaxTestOutputLength)) &&
            (test.StandardError is null || SafeText.IsMultiline(test.StandardError, MaxTestOutputLength)) &&
            test.Classification is TestClassifications.Flaky or TestClassifications.CodeIssue &&
            SafeText.IsSingleLine(test.Reason, MaxReasonLength);
}
