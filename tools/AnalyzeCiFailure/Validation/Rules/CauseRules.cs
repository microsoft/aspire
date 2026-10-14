// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using AnalyzeCiFailure.Common;
using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Common.WorkflowCommands;
using static AnalyzeCiFailure.Validation.ValidationException;
using static AnalyzeCiFailure.Validation.ValidationPolicy;

namespace AnalyzeCiFailure.Validation;

// Rules for cause files. A cause is persisted across runs, so these protect stored history as
// well as the current publication.
internal static partial class ValidationRules
{
    /// <summary>
    /// A cause file must be well formed and consistent with the analysis and the trusted jobs.
    /// Returns the validated cause.
    /// </summary>
    public static Cause CauseMustBeValid(string fileName, CauseDocument cause, Analysis analysis, List<TrustedJob> trustedJobs, TrustedRun run)
    {
        var name = Display(fileName);
        Require(IsWellFormedCause(cause), ValidationMessages.UnsupportedCauseFields(fileName));
        var id = cause.Id!;
        var type = cause.Type!;
        var jobIds = cause.JobIds!;

        // The ID is the stable key used to match this cause across runs.
        Require(CauseId().IsMatch(id) && $"{id}.json" == fileName, $"Cause ID must be a lowercase hyphenated slug matching its filename: {name}");
        Require(analysis.CauseIds.Contains(id), $"Cause {name} is not referenced by the analysis summary");
        Require(
            AllowedCauseTypes.TryGetValue(run.Scope, out var allowedTypes) && allowedTypes.Contains(type),
            $"Cause {name} type {Display(type)} is not permitted for run scope {Display(run.Scope)}");
        Require(
            jobIds.All(jobId => IsCompatibleJob(jobId, type, analysis, trustedJobs)),
            ValidationMessages.IncompatibleCauseJob(fileName));

        return new Cause(fileName, type, cause.TestName ?? "", jobIds);
    }

    /// <summary>
    /// A cause with the same ID was stored by an earlier run. Changing what it describes would merge
    /// unrelated failures into one history.
    /// </summary>
    public static void CauseMustMatchPriorCause(Cause cause, PriorCauseDocument prior)
    {
        var name = Display(cause.FileName);
        var priorType = prior.Type ?? "";
        Require(priorType == cause.Type, $"Cause {name} cannot change type from {Display(priorType)} to {Display(cause.Type)}");
        Require(
            cause.Type != CauseTypes.FlakyTest || (prior.TestName ?? "") == cause.TestName,
            $"Cause {name} cannot change stored test_name");
    }

    /// <summary>The summary's cause list and the cause files must name the same causes.</summary>
    public static void CauseIdsMustMatchCauseFiles(Analysis analysis, List<Cause> causes)
        => Require(
            analysis.CauseIds.Distinct().Count() == analysis.CauseIds.Count && analysis.CauseIds.Count == causes.Count,
            "Analysis cause IDs must uniquely match the generated cause files");

    /// <summary>
    /// Every failure that will be persisted must be covered by a cause for the same job (and test),
    /// so stored history never points at the wrong job.
    /// </summary>
    public static void CausesMustCoverPersistedFailures(Analysis analysis, List<Cause> causes, List<TrustedJob> trustedJobs)
    {
        // Every job with a persisted classification is listed by a cause of the matching type.
        foreach (var job in analysis.FailedJobs)
        {
            var causeType = CauseTypeFor(job.Classification);
            Require(
                causeType is null || causes.Any(cause => cause.Type == causeType && cause.JobIds.Contains(job.Id)),
                "Every transient, flaky, and main-breakage failed job must be covered by a matching cause");
        }

        var flakyTests = analysis.FailedTests.Where(test => test.Classification == TestClassifications.Flaky).ToList();
        string TrustedJobName(long jobId) => trustedJobs.FirstOrDefault(job => job.Id == jobId)?.Name ?? "";

        // Every flaky-test cause names a validated flaky test, and that test failed in each listed job.
        foreach (var cause in causes.Where(cause => cause.Type == CauseTypes.FlakyTest))
        {
            Require(flakyTests.Any(test => test.Name == cause.TestName), "Flaky-test cause must reference a validated flaky test");
            Require(
                cause.JobIds.All(jobId => flakyTests.Any(test => test.Name == cause.TestName && test.Job == TrustedJobName(jobId))),
                ValidationMessages.IncompatibleCauseJob(cause.FileName));
        }

        // Every flaky test is covered by a flaky-test cause that lists the job it failed in.
        Require(
            flakyTests.All(test => causes.Any(cause =>
                cause.Type == CauseTypes.FlakyTest &&
                cause.TestName == test.Name &&
                cause.JobIds.Any(jobId => trustedJobs.Any(job => job.Id == jobId && job.Name == test.Job)))),
            "Every flaky test and job must be covered by a matching cause");
    }

    private static bool IsWellFormedCause(CauseDocument cause)
        => cause.Id is not null &&
            cause.Type is not null &&
            SafeText.IsSingleLine(cause.Title, MaxCauseTitleLength) && SafeText.HasVisibleCharacter(cause.Title!) &&
            SafeText.IsMultiline(cause.ErrorPattern, MaxErrorPatternLength) && SafeText.HasVisibleCharacter(cause.ErrorPattern!) &&
            cause.JobIds is { Count: > 0 } jobIds && jobIds.All(jobId => jobId > 0) && jobIds.Distinct().Count() == jobIds.Count &&
            (cause.TestName is null || SafeText.IsSingleLine(cause.TestName, MaxTestNameLength)) &&
            // Infrastructure failures are not tied to a test.
            (cause.Type != CauseTypes.InfraFailure || string.IsNullOrEmpty(cause.TestName));

    /// <summary>Whether a cause of <paramref name="causeType"/> may list the trusted failed job <paramref name="jobId"/>.</summary>
    private static bool IsCompatibleJob(long jobId, string causeType, Analysis analysis, List<TrustedJob> trustedJobs)
    {
        var trustedJob = trustedJobs.FirstOrDefault(job => job.Id == jobId);
        if (trustedJob is null)
        {
            return false;
        }

        var classification = analysis.FailedJobs.FirstOrDefault(job => job.Id == jobId)?.Classification ?? "";
        return causeType switch
        {
            CauseTypes.InfraFailure => classification == JobClassifications.TransientInfra,
            CauseTypes.MainRepositoryBreakage => classification == JobClassifications.MainRepositoryBreakage,

            // A flaky test can share a failed job with deterministic failures. The job's primary
            // classification is then not flaky-test, so require validated flaky-test evidence that
            // names the same trusted job.
            _ => classification == JobClassifications.FlakyTest ||
                (classification is JobClassifications.CodeIssue or JobClassifications.MainRepositoryBreakage &&
                    analysis.FailedTests.Any(test => test.Classification == TestClassifications.Flaky && test.Job == trustedJob.Name)),
        };
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*\\z")]
    private static partial Regex CauseId();
}
