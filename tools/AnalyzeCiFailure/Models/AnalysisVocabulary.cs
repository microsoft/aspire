// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Identifies the supported main-branch and pull-request analysis contexts.</summary>
internal enum RunScope
{
    [JsonStringEnumMemberName("main")] Main = 1,
    [JsonStringEnumMemberName("pull-request")] PullRequest,
}

/// <summary>Distinguishes complete, inapplicable and unavailable structured test evidence.</summary>
internal enum EvidenceState
{
    [JsonStringEnumMemberName("complete")] Complete = 1,
    [JsonStringEnumMemberName("not-applicable")] NotApplicable,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
}

/// <summary>Distinguishes usable, incomplete and unavailable merge-comparison history.</summary>
internal enum CandidateHistoryState
{
    [JsonStringEnumMemberName("available")] Available = 1,
    [JsonStringEnumMemberName("incomplete")] Incomplete,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
}

/// <summary>Describes the overall diagnosis derived from accepted job, test and cause decisions.</summary>
internal enum AnalysisVerdict
{
    [JsonStringEnumMemberName("transient-infra")] TransientInfrastructure = 1,
    [JsonStringEnumMemberName("flaky-test")] FlakyTest,
    [JsonStringEnumMemberName("code-issue")] CodeIssue,
    [JsonStringEnumMemberName("main-repository-breakage")] MainRepositoryBreakage,
    [JsonStringEnumMemberName("mixed")] Mixed,
}

/// <summary>Defines the classifications permitted for failed or interrupted workflow jobs.</summary>
internal enum JobClassification
{
    [JsonStringEnumMemberName("transient-infra")] TransientInfrastructure = 1,
    [JsonStringEnumMemberName("flaky-test")] FlakyTest,
    [JsonStringEnumMemberName("code-issue")] CodeIssue,
    [JsonStringEnumMemberName("main-repository-breakage")] MainRepositoryBreakage,
}

/// <summary>Defines the classifications permitted for individual structured test observations.</summary>
internal enum TestClassification
{
    [JsonStringEnumMemberName("flaky")] Flaky = 1,
    [JsonStringEnumMemberName("code-issue")] CodeIssue,
    [JsonStringEnumMemberName("transient-infra")] TransientInfrastructure,
}

/// <summary>Defines the durable recurring-cause categories supported by history and issue publication.</summary>
internal enum CauseType
{
    [JsonStringEnumMemberName("infra-failure")] InfrastructureFailure = 1,
    [JsonStringEnumMemberName("flaky-test")] FlakyTest,
    [JsonStringEnumMemberName("main-repository-breakage")] MainRepositoryBreakage,
}
