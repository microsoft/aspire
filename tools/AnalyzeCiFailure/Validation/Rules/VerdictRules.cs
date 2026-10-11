// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Validation.ValidationException;

namespace AnalyzeCiFailure.Validation;

// Rules that tie the verdict, the job and test classifications, and the cause types into one story.
internal static partial class ValidationRules
{
    public static void VerdictMustMatchClassifications(string verdict, ClassificationCounts counts, TrustedRun run)
    {
        switch (verdict)
        {
            case Verdicts.TransientInfra:
                Require(counts.FailedTests == 0, "Analysis failed_tests are incompatible with verdict transient-infra");
                Require(
                    counts.InfraJobs == counts.FailedJobs && counts.Causes > 0 && counts.InfraCauses == counts.Causes,
                    "A transient-infra verdict requires every failed job and cause to be an infrastructure failure");
                break;

            case Verdicts.FlakyTest:
                Require(counts.CodeIssueTests == 0, "Analysis failed_tests are incompatible with verdict flaky-test");
                Require(
                    counts.FlakyJobs > 0 && counts.TransientJobs == counts.FailedJobs &&
                    counts.Causes > 0 && counts.FlakyCauses > 0 && counts.MainBreakCauses == 0,
                    "A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes");
                break;

            case Verdicts.CodeIssue:
                // Code issues belong to the PR, so they are reported but never stored as causes.
                Require(counts.FlakyTests == 0, "Analysis failed_tests are incompatible with verdict code-issue");
                Require(
                    counts.CodeIssueJobs == counts.FailedJobs && counts.Causes == 0,
                    "A code-issue verdict requires every failed job to be a code issue and must not include cause files");
                break;

            case Verdicts.MainRepositoryBreakage:
                Require(counts.FlakyTests == 0, "Analysis failed_tests are incompatible with verdict main-repository-breakage");
                Require(
                    counts.MainBreakJobs == counts.FailedJobs && counts.MainBreakCauses > 0 && counts.MainBreakCauses == counts.Causes,
                    "A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage");
                break;

            // "mixed" means a deterministic failure plus at least one transient failure, each with
            // its own evidence.
            case Verdicts.Mixed when run.Scope == RunScopes.Main:
                Require(
                    counts.MainBreakJobs > 0 && (counts.TransientJobs > 0 || counts.FlakyTests > 0) &&
                    counts.MainBreakCauses > 0 && counts.MainBreakCauses != counts.Causes,
                    "A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause");
                break;

            case Verdicts.Mixed when run.Scope == RunScopes.PullRequest:
                Require(
                    counts.CodeIssueJobs > 0 && (counts.TransientJobs > 0 || counts.FlakyTests > 0) && counts.Causes > 0,
                    "A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause");
                break;
        }
    }

    /// <summary>Each kind of persisted failure appears among the jobs exactly when a cause of that type exists.</summary>
    public static void ClassificationsMustMatchCauseTypes(ClassificationCounts counts)
        => Require(
            (counts.InfraJobs > 0) == (counts.InfraCauses > 0) &&
            (counts.FlakyJobs > 0 || counts.FlakyTests > 0) == (counts.FlakyCauses > 0) &&
            (counts.MainBreakJobs > 0) == (counts.MainBreakCauses > 0),
            "Failed-job classifications and persisted cause types do not match");
}
