// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.Json;

namespace AnalyzeCiFailure;

internal static class ValidationRules
{
    internal const int MaximumCauses = 10;
    internal const int MaximumCommentBytes = 65000;
    internal const string TestMismatch = "Analysis failed_tests do not match trusted test failure evidence";

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ValidationException(message);
        }
    }

    internal static void RequireRunIdentity(TrustedRunIdentity trusted, ProposedRunIdentity proposed)
    {
        Require(trusted.MetadataId == trusted.Id &&
            Regex.IsMatch(trusted.Url, $@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/{Regex.Escape(trusted.Id)}\z"),
            "Trusted run metadata is invalid");
        Require(proposed.Id == trusted.Id && proposed.Scope == trusted.Scope,
            "Analysis result does not match trusted run context");
        switch (trusted.Scope)
        {
            case "main":
                Require(!proposed.HasMainSubjectPr, "Main run analysis must not identify a subject PR");
                break;
            case "pull-request":
                Require(trusted.PrNumbers.Length == 0 || Regex.IsMatch(trusted.PrNumbers, "^[0-9]+\\z"),
                    "Trusted run context must contain one unambiguous subject PR");
                Require(proposed.ExplicitNullSubjectPr ||
                    (proposed.SubjectPrNumber is not null && trusted.PrNumbers.Length > 0 && proposed.SubjectPrNumber == trusted.PrNumbers),
                    "Pull request analysis must identify a trusted subject PR");
                break;
        }
    }

    internal static void RequirePermittedVerdict(FailureAnalysis analysis, ValidatedRun run)
    {
        var permitted = run.Scope switch
        {
            "main" => analysis.Verdict is "transient-infra" or "flaky-test" or "main-repository-breakage" or "mixed",
            "pull-request" => analysis.Verdict is "transient-infra" or "flaky-test" or "code-issue" or "mixed",
            _ => false,
        };
        Require(permitted, $"Verdict {Display(analysis.Verdict)} is not permitted for run scope {Display(run.Scope)}");
    }

    internal static void RequireExactJobCoverage(FailureAnalysis analysis, ImmutableArray<FailedJob> jobs, ValidatedRun run)
    {
        Require(analysis.Jobs.Length > 0 && analysis.Jobs.All(j => j.Classification is "transient-infra" or "flaky-test" or "code-issue" or "main-repository-breakage"),
            "Analysis must classify every failed job with a recognized classification");
        var ids = analysis.Jobs.Select(j => j.Id).ToArray();
        // Failed-job summaries must match the trusted numeric representation as well as its
        // value. Cause references and uniqueness use mathematical numeric identity instead.
        Require(ids.Distinct().Count() == ids.Length && ids.Length == jobs.Length &&
            analysis.Jobs.All(reported => jobs.Any(trusted => reported.Id == trusted.Id && reported.SerializedId == trusted.SerializedId)),
            "Analysis failed-job IDs do not match the trusted failed jobs");
        Require(!analysis.Jobs.Any(j => (run.Scope == "main" && j.Classification == "code-issue") ||
            (run.Scope == "pull-request" && j.Classification == "main-repository-breakage")),
            $"Analysis contains a failed-job classification that is not permitted for run scope {Display(run.Scope)}");
    }

    internal static void RequireExactTestCoverage(FailureAnalysis analysis, ImmutableArray<TestIdentity> trusted, ImmutableArray<FailedJob> jobs)
    {
        var reported = analysis.Tests.Select(t => new TestIdentity(t.Name, t.Job)).ToArray();
        Require(reported.Distinct().Count() == reported.Length &&
            trusted.Distinct().Count() == trusted.Length &&
            reported.ToHashSet().SetEquals(trusted) &&
            analysis.Tests.All(t => jobs.Any(j => j.Name == t.Job)), TestMismatch);
    }

    internal static void ValidateCauseCompatibility(FailureCause cause, FailureAnalysis analysis, ImmutableArray<FailedJob> jobs, ValidatedRun run)
    {
        Require(analysis.CauseIds.Contains(cause.Id), $"Cause {Display(cause.FileName)} is not referenced by the analysis summary");
        Require(cause.Type is "flaky-test" or "infra-failure" || (run.Scope == "main" && cause.Type == "main-repository-breakage"),
            $"Cause {Display(cause.FileName)} type {Display(cause.Type)} is not permitted for run scope {Display(run.Scope)}");
        foreach (var jobId in cause.JobIds)
        {
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            var classification = analysis.Jobs.FirstOrDefault(j => j.Id == jobId)?.Classification;
            var hasFlakyTestInJob = job is not null && analysis.Tests.Any(t => t.Classification == "flaky" && t.Job == job.Name);
            var compatible = cause.Type switch
            {
                "infra-failure" => classification == "transient-infra",
                "main-repository-breakage" => classification == "main-repository-breakage",
                "flaky-test" => classification == "flaky-test" ||
                    (classification is "code-issue" or "main-repository-breakage" && hasFlakyTestInJob),
                _ => false,
            };
            Require(job is not null && compatible, IncompatibleJob(cause.FileName));
        }
    }

    internal static void RequireCauseFields(FailureCause cause)
    {
        Require(cause.JobIds.Length > 0 &&
            cause.JobIds.Distinct().Count() == cause.JobIds.Length &&
            (cause.Type != "infra-failure" || cause.TestName.Length == 0),
            $"Cause {Display(cause.FileName)} contains unsupported or publisher-owned fields");
    }

    internal static void RequireUnchangedCauseIdentity(FailureCause cause, StoredCauseIdentity prior)
    {
        Require(prior.Type == cause.Type, $"Cause {Display(cause.FileName)} cannot change type from {Display(prior.Type)} to {Display(cause.Type)}");
        Require(cause.Type != "flaky-test" || prior.TestName == cause.TestName,
            $"Cause {Display(cause.FileName)} cannot change stored test_name");
    }

    internal static void RequireExactCauseCoverage(FailureAnalysis analysis, ImmutableArray<FailureCause> causes)
    {
        Require(analysis.CauseIds.Distinct().Count() == analysis.CauseIds.Length && analysis.CauseIds.Length == causes.Length,
            "Analysis cause IDs must uniquely match the generated cause files");
    }

    internal static void ValidateVerdict(FailureAnalysis analysis, ImmutableArray<FailureCause> causes, ValidatedRun run)
    {
        var infraJobs = analysis.Jobs.Count(j => j.Classification == "transient-infra");
        var flakyJobs = analysis.Jobs.Count(j => j.Classification == "flaky-test");
        var codeJobs = analysis.Jobs.Count(j => j.Classification == "code-issue");
        var mainJobs = analysis.Jobs.Count(j => j.Classification == "main-repository-breakage");
        var hasFlakyTests = analysis.Tests.Any(t => t.Classification == "flaky");
        var hasFlakyEvidence = flakyJobs > 0 || hasFlakyTests;
        var hasTransientEvidence = infraJobs > 0 || hasFlakyEvidence;
        var infraCauses = causes.Count(c => c.Type == "infra-failure");
        var flakyCauses = causes.Count(c => c.Type == "flaky-test");
        var mainCauses = causes.Count(c => c.Type == "main-repository-breakage");
        switch (analysis.Verdict)
        {
            case "transient-infra":
                Require(analysis.Tests.Length == 0, "Analysis failed_tests are incompatible with verdict transient-infra");
                Require(infraJobs == analysis.Jobs.Length && causes.Length > 0 && infraCauses == causes.Length,
                    "A transient-infra verdict requires every failed job and cause to be an infrastructure failure");
                break;
            case "flaky-test":
                Require(!analysis.Tests.Any(t => t.Classification == "code-issue"), "Analysis failed_tests are incompatible with verdict flaky-test");
                Require(flakyJobs > 0 && infraJobs + flakyJobs == analysis.Jobs.Length && causes.Length > 0 && flakyCauses > 0 && mainCauses == 0,
                    "A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes");
                break;
            case "code-issue":
                Require(!hasFlakyTests, "Analysis failed_tests are incompatible with verdict code-issue");
                Require(codeJobs == analysis.Jobs.Length && causes.Length == 0,
                    "A code-issue verdict requires every failed job to be a code issue and must not include cause files");
                break;
            case "main-repository-breakage":
                Require(!hasFlakyTests, "Analysis failed_tests are incompatible with verdict main-repository-breakage");
                Require(mainJobs == analysis.Jobs.Length && mainCauses > 0 && mainCauses == causes.Length,
                    "A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage");
                break;
            case "mixed" when run.Scope == "main":
                Require(mainJobs > 0 && hasTransientEvidence && mainCauses > 0 && mainCauses != causes.Length,
                    "A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause");
                break;
            case "mixed":
                Require(codeJobs > 0 && hasTransientEvidence && causes.Length > 0,
                    "A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause");
                break;
        }
        Require((infraJobs > 0) == (infraCauses > 0) &&
            hasFlakyEvidence == (flakyCauses > 0) &&
            (mainJobs > 0) == (mainCauses > 0),
            "Failed-job classifications and persisted cause types do not match");
    }

    internal static void RequireCauseCoverage(FailureAnalysis analysis, ImmutableArray<FailureCause> causes, ImmutableArray<FailedJob> jobs)
    {
        foreach (var job in analysis.Jobs)
        {
            var type = job.Classification switch
            {
                "transient-infra" => "infra-failure",
                "flaky-test" => "flaky-test",
                "main-repository-breakage" => "main-repository-breakage",
                _ => null,
            };
            Require(type is null || causes.Any(c => c.Type == type && c.JobIds.Contains(job.Id)),
                "Every transient, flaky, and main-breakage failed job must be covered by a matching cause");
        }

        var flaky = analysis.Tests.Where(t => t.Classification == "flaky").ToArray();
        foreach (var cause in causes.Where(c => c.Type == "flaky-test"))
        {
            Require(flaky.Any(t => t.Name == cause.TestName), "Flaky-test cause must reference a validated flaky test");
            Require(cause.JobIds.All(id => flaky.Any(t => t.Name == cause.TestName && t.Job == jobs.First(j => j.Id == id).Name)),
                IncompatibleJob(cause.FileName));
        }

        // With no persisted causes, verdict validation determines whether analysis can
        // publish. Test-to-cause joins apply only when causes are present.
        if (causes.Length > 0)
        {
            Require(flaky.All(t => causes.Any(c => c.Type == "flaky-test" && c.TestName == t.Name &&
                c.JobIds.Any(id => jobs.Any(j => j.Id == id && j.Name == t.Job)))),
                "Every flaky test and job must be covered by a matching cause");
        }
    }

    internal static void RequireCauseBudget(int summaryCount, int fileCount)
        => Require(summaryCount <= MaximumCauses && fileCount <= MaximumCauses, "Analysis exceeds the 10-cause publication budget");

    internal static void RequireCommentBudget(int? commentBytes)
    {
        Require(commentBytes is not null, "Unable to render the PR comment during validation");
        Require(commentBytes <= MaximumCommentBytes, "Rendered PR comment exceeds the 65000-byte publication budget");
    }

    private static string IncompatibleJob(string name) => $"Cause {Display(name)} references an unknown or incompatible failed job";
}
