// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Enforces evidence coverage, cause compatibility, identity and attribution rules for a diagnosis.</summary>
internal static class ValidationRules
{
    internal const int MaximumIndividualCauseIssues = 10;
    internal const int MaximumCommentBytes = 65000;
    internal const string TestMismatch = "Analysis failed_tests do not match trusted test failure evidence";

    internal static void RequirePermittedVerdict(AnalysisReport analysis, ValidatedRun run)
    {
        var permitted = run.Scope switch
        {
            RunScope.Main => analysis.Verdict is AnalysisVerdict.TransientInfrastructure or AnalysisVerdict.FlakyTest or AnalysisVerdict.MainRepositoryBreakage or AnalysisVerdict.Mixed,
            RunScope.PullRequest => analysis.Verdict is AnalysisVerdict.TransientInfrastructure or AnalysisVerdict.FlakyTest or AnalysisVerdict.CodeIssue or AnalysisVerdict.Mixed,
            _ => false,
        };
        if (!permitted)
        {
            throw new ValidationException($"Verdict {EnumText(analysis.Verdict.GetValueOrDefault())} is not permitted for run scope {EnumText(run.Scope)}");
        }
    }

    internal static void RequireExactJobCoverage(AnalysisReport analysis, ImmutableArray<FailedJob> jobs, ValidatedRun run)
    {
        if (!(analysis.Jobs.Length > 0 && analysis.Jobs.All(j => j.Classification is JobClassification.TransientInfrastructure or JobClassification.FlakyTest or JobClassification.CodeIssue or JobClassification.MainRepositoryBreakage)))
        {
            throw new ValidationException("Analysis must classify every failed job with a recognized classification");
        }
        var ids = analysis.Jobs.Select(j => j.NormalizedId).ToArray();
        // Failed-job summaries must match the trusted numeric representation as well as its
        // value. Cause references and uniqueness use mathematical numeric identity instead.
        if (!(ids.Distinct().Count() == ids.Length && ids.Length == jobs.Length &&
            analysis.Jobs.All(reported => jobs.Any(collected => reported.NormalizedId == collected.Id && reported.SerializedId == collected.SerializedId))))
        {
            throw new ValidationException("Analysis failed-job IDs do not match the trusted failed jobs");
        }
        if (analysis.Jobs.Any(j => (run.Scope == RunScope.Main && j.Classification == JobClassification.CodeIssue) ||
            (run.Scope == RunScope.PullRequest && j.Classification == JobClassification.MainRepositoryBreakage)))
        {
            throw new ValidationException($"Analysis contains a failed-job classification that is not permitted for run scope {EnumText(run.Scope)}");
        }
    }

    internal static ImmutableArray<int> RequireExactTestCoverage(AnalysisReport analysis, ImmutableArray<TestIdentity> collected, ImmutableArray<FailedJob> jobs)
    {
        var matched = ImmutableArray.CreateBuilder<int>();
        var used = new HashSet<int>();
        foreach (var test in analysis.Tests)
        {
            var candidates = Enumerable.Range(start: 0, collected.Length).Where(i => collected[i].Job == test.Job &&
                test.NormalizedJobId is not null && collected[i].Name == test.Name && collected[i].JobId == test.NormalizedJobId &&
                collected[i].ObservationId == test.ObservationId).ToArray();
            if (!(candidates.Length == 1 && used.Add(candidates[0])))
            {
                throw new ValidationException(TestMismatch);
            }
            matched.Add(candidates[0]);
        }
        if (!(used.Count == collected.Length &&
            analysis.Tests.All(t => jobs.Any(j => j.Name == t.Job))))
        {
            throw new ValidationException(TestMismatch);
        }

        return matched.ToImmutable();
    }

    internal static void ValidateCauseCompatibility(FailureCause cause, AnalysisReport analysis, ImmutableArray<FailedJob> jobs, ValidatedRun run)
    {
        if (!analysis.Causes.Contains(cause.Id))
        {
            throw new ValidationException($"Cause {Display(cause.FileName)} is not referenced by the analysis summary");
        }
        if (!(cause.Type is CauseType.FlakyTest or CauseType.InfrastructureFailure || (run.Scope == RunScope.Main && cause.Type == CauseType.MainRepositoryBreakage)))
        {
            throw new ValidationException($"Cause {Display(cause.FileName)} type {EnumText(cause.Type)} is not permitted for run scope {EnumText(run.Scope)}");
        }
        foreach (var jobId in cause.JobIds)
        {
            var job = jobs.FirstOrDefault(j => j.Id == jobId);
            var classification = analysis.Jobs.FirstOrDefault(j => j.NormalizedId == jobId)?.Classification;
            var hasFlakyTestInJob = job is not null && analysis.Tests.Any(t => t.Classification == TestClassification.Flaky && t.NormalizedJobId == job.Id);
            var compatible = cause.Type switch
            {
                CauseType.InfrastructureFailure => classification == JobClassification.TransientInfrastructure,
                CauseType.MainRepositoryBreakage => classification == JobClassification.MainRepositoryBreakage,
                CauseType.FlakyTest => classification == JobClassification.FlakyTest ||
                    (classification is JobClassification.CodeIssue or JobClassification.MainRepositoryBreakage && hasFlakyTestInJob),
                _ => false,
            };
            if (!(job is not null && compatible))
            {
                throw new ValidationException(IncompatibleJob(cause.FileName));
            }
        }
    }

    internal static void RequireCauseFields(FailureCause cause)
    {
        if (!(cause.JobIds.Length > 0 &&
            cause.JobIds.Distinct().Count() == cause.JobIds.Length &&
            (cause.Type == CauseType.FlakyTest ? !cause.Tests.IsDefault : cause.Tests.IsDefault)))
        {
            throw new ValidationException($"Cause {Display(cause.FileName)} contains unsupported or publisher-owned fields");
        }
    }

    internal static void RequireUnchangedCauseType(FailureCause cause, StoredCauseIdentity prior)
    {
        if (!(prior.Type == cause.Type))
        {
            throw new ValidationException($"Cause {Display(cause.FileName)} cannot change type from {EnumText(prior.Type)} to {EnumText(cause.Type)}");
        }
    }

    internal static void ValidateVerdict(AnalysisReport analysis, ImmutableArray<FailureCause> causes, ValidatedRun run)
    {
        var infraJobs = analysis.Jobs.Count(j => j.Classification == JobClassification.TransientInfrastructure);
        var flakyJobs = analysis.Jobs.Count(j => j.Classification == JobClassification.FlakyTest);
        var codeJobs = analysis.Jobs.Count(j => j.Classification == JobClassification.CodeIssue);
        var mainJobs = analysis.Jobs.Count(j => j.Classification == JobClassification.MainRepositoryBreakage);
        var hasFlakyTests = analysis.Tests.Any(t => t.Classification == TestClassification.Flaky);
        var hasFlakyEvidence = flakyJobs > 0 || hasFlakyTests;
        var hasTransientEvidence = infraJobs > 0 || hasFlakyEvidence;
        var infraCauses = causes.Count(c => c.Type == CauseType.InfrastructureFailure);
        var flakyCauses = causes.Count(c => c.Type == CauseType.FlakyTest);
        var mainCauses = causes.Count(c => c.Type == CauseType.MainRepositoryBreakage);
        switch (analysis.Verdict)
        {
            case AnalysisVerdict.TransientInfrastructure:
                if (analysis.Tests.Any(test => test.Classification != TestClassification.TransientInfrastructure))
                {
                    throw new ValidationException("Analysis failed_tests are incompatible with verdict transient-infra");
                }
                if (!(infraJobs == analysis.Jobs.Length && causes.Length > 0 && infraCauses == causes.Length))
                {
                    throw new ValidationException("A transient-infra verdict requires every failed job and cause to be an infrastructure failure");
                }
                break;
            case AnalysisVerdict.FlakyTest:
                if (analysis.Tests.Any(t => t.Classification == TestClassification.CodeIssue))
                {
                    throw new ValidationException("Analysis failed_tests are incompatible with verdict flaky-test");
                }
                if (!(flakyJobs > 0 && infraJobs + flakyJobs == analysis.Jobs.Length && causes.Length > 0 && flakyCauses > 0 && mainCauses == 0))
                {
                    throw new ValidationException("A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes");
                }
                break;
            case AnalysisVerdict.CodeIssue:
                if (hasFlakyTests)
                {
                    throw new ValidationException("Analysis failed_tests are incompatible with verdict code-issue");
                }
                if (!(codeJobs == analysis.Jobs.Length && causes.Length == 0))
                {
                    throw new ValidationException("A code-issue verdict requires every failed job to be a code issue and must not include cause files");
                }
                break;
            case AnalysisVerdict.MainRepositoryBreakage:
                if (hasFlakyTests)
                {
                    throw new ValidationException("Analysis failed_tests are incompatible with verdict main-repository-breakage");
                }
                if (!(mainJobs == analysis.Jobs.Length && mainCauses > 0 && mainCauses == causes.Length))
                {
                    throw new ValidationException("A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage");
                }
                break;
            case AnalysisVerdict.Mixed when run.Scope == RunScope.Main:
                if (!(mainJobs > 0 && hasTransientEvidence && mainCauses > 0 && mainCauses != causes.Length))
                {
                    throw new ValidationException("A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause");
                }
                break;
            case AnalysisVerdict.Mixed:
                if (!(codeJobs > 0 && hasTransientEvidence && causes.Length > 0))
                {
                    throw new ValidationException("A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause");
                }
                break;
        }
        RequireCauseClassificationCoverage(analysis, causes);
    }

    internal static void RequireCauseClassificationCoverage(AnalysisReport analysis, ImmutableArray<FailureCause> causes)
    {
        var infraJobs = analysis.Jobs.Count(job => job.Classification == JobClassification.TransientInfrastructure);
        var mainJobs = analysis.Jobs.Count(job => job.Classification == JobClassification.MainRepositoryBreakage);
        var hasFlakyEvidence = analysis.Jobs.Any(job => job.Classification == JobClassification.FlakyTest) ||
            analysis.Tests.Any(test => test.Classification == TestClassification.Flaky);
        var infraCauses = causes.Count(cause => cause.Type == CauseType.InfrastructureFailure);
        var flakyCauses = causes.Count(cause => cause.Type == CauseType.FlakyTest);
        var mainCauses = causes.Count(cause => cause.Type == CauseType.MainRepositoryBreakage);
        if (!((infraJobs > 0) == (infraCauses > 0) &&
            hasFlakyEvidence == (flakyCauses > 0) &&
            (mainJobs > 0) == (mainCauses > 0)))
        {
            throw new ValidationException("Failed-job classifications and persisted cause types do not match");
        }
    }

    internal static void RequireCauseCoverage(AnalysisReport analysis, ImmutableArray<FailureCause> causes)
    {
        foreach (var test in analysis.Tests.Where(test => test.Classification == TestClassification.TransientInfrastructure))
        {
            if (!analysis.Jobs.Any(job => job.NormalizedId == test.NormalizedJobId && job.Classification == JobClassification.TransientInfrastructure) ||
                !causes.Any(cause => cause.Type == CauseType.InfrastructureFailure && cause.JobIds.Contains(test.NormalizedJobId!.Value)))
            {
                throw new ValidationException("Every infrastructure test observation must belong to an infrastructure job covered by an infrastructure cause.");
            }
        }
        foreach (var job in analysis.Jobs)
        {
            CauseType? type = job.Classification switch
            {
                JobClassification.TransientInfrastructure => CauseType.InfrastructureFailure,
                JobClassification.FlakyTest => CauseType.FlakyTest,
                JobClassification.MainRepositoryBreakage => CauseType.MainRepositoryBreakage,
                _ => null,
            };
            if (!(type is null || causes.Any(c => c.Type == type && c.JobIds.Contains(job.NormalizedId))))
            {
                throw new ValidationException("Every transient, flaky, and main-breakage failed job must be covered by a matching cause");
            }
        }

        var flaky = analysis.Tests.Where(t => t.Classification == TestClassification.Flaky).ToArray();
        foreach (var cause in causes.Where(c => c.Type == CauseType.FlakyTest))
        {
            if (!cause.Tests.All(o => cause.JobIds.Contains(o.JobId) &&
                flaky.Any(t => t.Name == o.Name && t.NormalizedJobId == o.JobId && t.ObservationId == o.ObservationId)))
            {
                throw new ValidationException("Cause test observation must reference a validated flaky test in its trusted job");
            }
            if (!cause.JobIds.All(id => cause.Tests.Any(o => o.JobId == id)))
            {
                throw new ValidationException("Every cause job must contain a referenced test observation");
            }
        }

        // With no persisted causes, verdict validation determines whether analysis can
        // publish. Test-to-cause joins apply only when causes are present.
        if (causes.Length > 0)
        {
            if (!flaky.All(t => causes.Any(c => c.Type == CauseType.FlakyTest &&
                c.Tests.Any(o => o.Name == t.Name && o.JobId == t.NormalizedJobId && o.ObservationId == t.ObservationId))))
            {
                throw new ValidationException("Every flaky test and job observation must be covered by a matching cause");
            }
        }
    }

    internal static bool UsesAggregatePublication(int causeCount) => causeCount > MaximumIndividualCauseIssues;

    internal static void RequireCommentBudget(int? commentBytes)
    {
        if (!(commentBytes is not null))
        {
            throw new ValidationException("Unable to render the PR comment during validation");
        }
        if (!(commentBytes <= MaximumCommentBytes))
        {
            throw new ValidationException("Rendered PR comment exceeds the 65000-byte publication budget");
        }
    }

    private static string IncompatibleJob(string name) => $"Cause {Display(name)} references an unknown or incompatible failed job";
}
