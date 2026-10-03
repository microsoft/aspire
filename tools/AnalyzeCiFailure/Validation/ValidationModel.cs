// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Contracts;

namespace AnalyzeCiFailure.Validation;

// What the rules work with: the inputs after they have been read and their shape checked. The
// records under Contracts mirror the JSON files; these hold only values that have passed validation.

/// <summary>The run being analyzed, established from data the workflow collected itself.</summary>
/// <param name="PrNumber">The single PR the run belongs to, or null when there is none.</param>
internal sealed record TrustedRun(long Id, string Scope, string Url, long? PrNumber);

/// <summary>Whether test results could be collected, and the failures they contain when they could.</summary>
/// <param name="Failures">Present only when <see cref="State"/> is <c>complete</c>.</param>
internal sealed record TestEvidence(string State, List<TrustedTestFailure>? Failures);

/// <summary>The validated parts of analysis-result.json.</summary>
internal sealed record Analysis(
    string Verdict,
    List<string> CauseIds,
    List<FailedJob> FailedJobs,
    List<FailedTest> FailedTests);

internal sealed record FailedJob(long Id, string? Classification);

internal sealed record FailedTest(string Name, string Job, string Classification);

/// <summary>A validated cause file. <see cref="TestName"/> is empty when the cause has no test.</summary>
internal sealed record Cause(string FileName, string Type, string TestName, List<long> JobIds);

/// <summary>How many jobs, tests, and causes the analysis puts in each category.</summary>
internal sealed record ClassificationCounts(
    int FailedJobs,
    int InfraJobs,
    int FlakyJobs,
    int CodeIssueJobs,
    int MainBreakJobs,
    int FailedTests,
    int FlakyTests,
    int CodeIssueTests,
    int Causes,
    int InfraCauses,
    int FlakyCauses,
    int MainBreakCauses)
{
    /// <summary>Jobs whose failure is expected to pass on retry.</summary>
    public int TransientJobs => InfraJobs + FlakyJobs;

    public static ClassificationCounts From(Analysis analysis, List<Cause> causes)
    {
        int Jobs(string classification) => analysis.FailedJobs.Count(job => job.Classification == classification);
        int Tests(string classification) => analysis.FailedTests.Count(test => test.Classification == classification);
        int Causes(string type) => causes.Count(cause => cause.Type == type);

        return new(
            analysis.FailedJobs.Count,
            Jobs(JobClassifications.TransientInfra),
            Jobs(JobClassifications.FlakyTest),
            Jobs(JobClassifications.CodeIssue),
            Jobs(JobClassifications.MainRepositoryBreakage),
            analysis.FailedTests.Count,
            Tests(TestClassifications.Flaky),
            Tests(TestClassifications.CodeIssue),
            causes.Count,
            Causes(CauseTypes.InfraFailure),
            Causes(CauseTypes.FlakyTest),
            Causes(CauseTypes.MainRepositoryBreakage));
    }
}
