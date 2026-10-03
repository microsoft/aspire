// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Common.WorkflowCommands;

namespace AnalyzeCiFailure.Validation;

/// <summary>The limits and per-scope permissions the rules enforce.</summary>
internal static class ValidationPolicy
{
    /// <summary>Bounds the issues and comments one run can create.</summary>
    public const int MaxCauseCount = 10;

    /// <summary>The same body budget the issue renderer (analyze-ci-failure-issue.sh) enforces.</summary>
    public const int MaxCommentBytes = 65000;

    // Lengths of agent-written text, in code points. Everything below can appear in a PR comment or
    // an issue; the limits keep the comment within budget.
    public const int MaxReasonLength = 500;
    public const int MaxTestNameLength = 500;
    public const int MaxJobNameLength = 500;
    public const int MaxTestErrorLength = 1000;
    public const int MaxStackTraceLength = 2000;
    public const int MaxTestOutputLength = 4000;
    public const int MaxCauseTitleLength = 238;
    public const int MaxErrorPatternLength = 500;

    /// <summary>
    /// Only a PR can be blamed for a code issue, and only main can have a main-repository breakage.
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedVerdicts = new()
    {
        [RunScopes.Main] = [Verdicts.TransientInfra, Verdicts.FlakyTest, Verdicts.MainRepositoryBreakage, Verdicts.Mixed],
        [RunScopes.PullRequest] = [Verdicts.TransientInfra, Verdicts.FlakyTest, Verdicts.CodeIssue, Verdicts.Mixed],
    };

    /// <summary>The same split as <see cref="AllowedVerdicts"/>, for each failed job.</summary>
    public static readonly Dictionary<string, string> DisallowedJobClassification = new()
    {
        [RunScopes.Main] = JobClassifications.CodeIssue,
        [RunScopes.PullRequest] = JobClassifications.MainRepositoryBreakage,
    };

    /// <summary>
    /// A PR run never persists a main-repository-breakage cause: a failure caused by the PR's own
    /// changes is a code issue, and code issues are reported on the PR rather than stored.
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedCauseTypes = new()
    {
        [RunScopes.Main] = [CauseTypes.FlakyTest, CauseTypes.InfraFailure, CauseTypes.MainRepositoryBreakage],
        [RunScopes.PullRequest] = [CauseTypes.FlakyTest, CauseTypes.InfraFailure],
    };

    /// <summary>The cause type that must cover a failed job, or null when the job's failure is not persisted.</summary>
    public static string? CauseTypeFor(string? jobClassification) => jobClassification switch
    {
        JobClassifications.TransientInfra => CauseTypes.InfraFailure,
        JobClassifications.FlakyTest => CauseTypes.FlakyTest,
        JobClassifications.MainRepositoryBreakage => CauseTypes.MainRepositoryBreakage,
        _ => null,
    };
}

/// <summary>Validation errors reported from more than one place.</summary>
internal static class ValidationMessages
{
    public const string RunContextMismatch = "Analysis result does not match trusted run context";
    public const string MainRunHasPullRequest = "Main run analysis must not identify a subject PR";
    public const string UntrustedPullRequest = "Pull request analysis must identify a trusted subject PR";
    public const string InvalidIdArrays = "Analysis must contain numeric-ID failed_jobs and string-valued causes arrays";
    public const string UnsafeFailedTests = "Analysis failed_tests must match the safe field schema";
    public const string UnclassifiedJobs = "Analysis must classify every failed job with a recognized classification";
    public const string TestEvidenceMismatch = "Analysis failed_tests do not match trusted test failure evidence";

    public static string UnsupportedCauseFields(string causeFileName)
        => $"Cause {Display(causeFileName)} contains unsupported or publisher-owned fields";

    public static string IncompatibleCauseJob(string causeFileName)
        => $"Cause {Display(causeFileName)} references an unknown or incompatible failed job";
}
