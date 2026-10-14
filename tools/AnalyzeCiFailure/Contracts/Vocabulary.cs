// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure.Contracts;

/// <summary>Values defined by the analysis output contract in .github/workflows/analyze-ci-failure.md.</summary>
internal static class RunScopes
{
    public const string Main = "main";
    public const string PullRequest = "pull-request";
}

internal static class Verdicts
{
    public const string TransientInfra = "transient-infra";
    public const string FlakyTest = "flaky-test";
    public const string CodeIssue = "code-issue";
    public const string MainRepositoryBreakage = "main-repository-breakage";
    public const string Mixed = "mixed";
}

/// <summary>Classification of each entry in the analysis <c>failed_jobs</c> array.</summary>
internal static class JobClassifications
{
    public const string TransientInfra = "transient-infra";
    public const string FlakyTest = "flaky-test";
    public const string CodeIssue = "code-issue";
    public const string MainRepositoryBreakage = "main-repository-breakage";

    public static readonly string[] All = [TransientInfra, FlakyTest, CodeIssue, MainRepositoryBreakage];
}

/// <summary>Classification of each entry in the analysis <c>failed_tests</c> array.</summary>
internal static class TestClassifications
{
    public const string Flaky = "flaky";
    public const string CodeIssue = "code-issue";
}

/// <summary>The <c>type</c> of a cause file. Causes are persisted, so only non-PR-specific failures have them.</summary>
internal static class CauseTypes
{
    public const string InfraFailure = "infra-failure";
    public const string FlakyTest = "flaky-test";
    public const string MainRepositoryBreakage = "main-repository-breakage";
}

/// <summary>The <c>state</c> of <c>ci-failure-data/test-evidence.json</c>.</summary>
internal static class TestEvidenceStates
{
    /// <summary>Every failed job's test-results artifact was downloaded and parsed.</summary>
    public const string Complete = "complete";

    /// <summary>No failed job has a test-results artifact.</summary>
    public const string NotApplicable = "not-applicable";

    /// <summary>The test-results artifacts could not be listed, downloaded, or extracted.</summary>
    public const string Unavailable = "unavailable";
}
