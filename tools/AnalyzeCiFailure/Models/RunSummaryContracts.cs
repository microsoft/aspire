// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Groups the overall verdict, job and test classifications, and referenced causes used by diagnosis consumers.</summary>
internal sealed record AnalysisReport
{
    [JsonPropertyName("verdict")]
    public AnalysisVerdict? Verdict { get; init; }
    [JsonPropertyName("failed_jobs")]
    public ImmutableArray<AnalysisJob> Jobs { get; init; } = [];
    [JsonPropertyName("failed_tests")]
    public ImmutableArray<AnalysisTest> Tests { get; init; } = [];
    [JsonPropertyName("causes")]
    public ImmutableArray<string> Causes { get; init; } = [];
}

/// <summary>Carries one job decision while preserving its raw identifier token for evidence matching.</summary>
internal sealed record AnalysisJob
{
    [JsonIgnore]
    internal NormalizedJsonNumber NormalizedId => CiFailureJson.AsNormalizedNumber(Id)!.Value;
    [JsonIgnore]
    internal string SerializedId => Id!.Value.GetRawText();
    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }
    [JsonPropertyName("classification")]
    public JobClassification? Classification { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Persists a run&apos;s accepted diagnosis, classified observations, cause references and attribution context.</summary>
internal sealed record RunSummaryRecord
{
    [JsonPropertyName("overflow_issue_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OverflowIssueUrl { get; init; }
    [JsonPropertyName("run_id")]
    public JsonElement? RunId { get; init; }
    [JsonPropertyName("run_attempt")]
    public JsonElement? RunAttempt { get; init; }
    [JsonPropertyName("run_url")]
    public required string RunUrl { get; init; }
    [JsonPropertyName("run_scope")]
    public RunScope? RunScope { get; init; }
    [JsonPropertyName("analyzed_at")]
    public required string AnalyzedAt { get; init; }
    [JsonPropertyName("verdict")]
    public AnalysisVerdict? Verdict { get; init; }
    [JsonPropertyName("pr")]
    public PullRequestSummary? PullRequest { get; init; }
    [JsonPropertyName("triggering_merge_pr")]
    public TriggeringMergeSummary? TriggeringMerge { get; init; }
    [JsonPropertyName("main_context")]
    public MainSummary? MainContext { get; init; }
    [JsonPropertyName("failed_jobs")]
    public required SummaryJob[] Jobs { get; init; }
    [JsonPropertyName("failed_tests")]
    public required SummaryTest[] Tests { get; init; }
    [JsonPropertyName("causes")]
    public string[]? Causes { get; init; }
}

/// <summary>Stores the PR identity and descriptive context displayed in a run summary.</summary>
internal record PullRequestSummary
{
    [JsonPropertyName("number")]
    public JsonElement? Number { get; init; }
    [JsonPropertyName("title")]
    public required string Title { get; init; }
    [JsonPropertyName("author")]
    public required string Author { get; init; }
    [JsonPropertyName("state")]
    public required string State { get; init; }
    [JsonPropertyName("head_branch")]
    public required string HeadBranch { get; init; }
    [JsonPropertyName("base_branch")]
    public required string BaseBranch { get; init; }
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>Adds merge timing to the pull-request context stored for a triggering main-branch merge.</summary>
internal sealed record TriggeringMergeSummary : PullRequestSummary
{
    [JsonPropertyName("merged_at"), JsonPropertyOrder(1)]
    public JsonElement? MergedAt { get; init; }
}

/// <summary>Records the failed and last-successful main revisions with merge-history availability.</summary>
internal sealed record MainSummary
{
    [JsonPropertyName("last_successful_main_sha")]
    public JsonElement? LastSuccessfulSha { get; init; }
    [JsonPropertyName("failed_sha")]
    public JsonElement? FailedSha { get; init; }
    [JsonPropertyName("candidate_merge_history_state")]
    public required CandidateHistoryState CandidateState { get; init; }
    [JsonPropertyName("candidate_merges")]
    public CandidateMerge[]? Candidates { get; init; }
}

/// <summary>Persists a job&apos;s identity, outcome, classification and failed-step display context.</summary>
internal sealed record SummaryJob
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }
    [JsonPropertyName("conclusion")]
    public JsonElement? Conclusion { get; init; }
    [JsonPropertyName("url")]
    public required string Url { get; init; }
    [JsonPropertyName("classification")]
    public JobClassification? Classification { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
    [JsonPropertyName("failed_steps")]
    public required string[] FailedSteps { get; init; }
}

/// <summary>Carries a classified test observation and its exact producing-job identity for validation and rendering.</summary>
internal sealed record AnalysisTest
{
    [JsonIgnore]
    internal NormalizedJsonNumber? NormalizedJobId => CiFailureJson.AsNormalizedNumber(JobId);
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
    [JsonPropertyName("job")]
    public string? Job { get; init; }
    [JsonPropertyName("error")]
    public string? Error { get; init; }
    [JsonPropertyName("stack_trace")]
    public string? StackTrace { get; init; }
    [JsonPropertyName("standard_output")]
    public string? StandardOutput { get; init; }
    [JsonPropertyName("standard_error")]
    public string? StandardError { get; init; }
    [JsonPropertyName("classification")]
    public TestClassification? Classification { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("job_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? JobId { get; init; }
    [JsonPropertyName("observation_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationId { get; init; }
    [JsonPropertyName("execution_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; init; }
    [JsonPropertyName("result_file"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultFile { get; init; }
    [JsonPropertyName("result_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TestArtifactFormat? ResultFormat { get; init; }
}

/// <summary>Persists a classified test observation with bounded diagnostics and source provenance.</summary>
internal record SummaryTest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("job")]
    public string? Job { get; init; }
    [JsonPropertyName("error")]
    public string? Error { get; init; }
    [JsonPropertyName("stack_trace")]
    public string? StackTrace { get; init; }
    [JsonPropertyName("standard_output")]
    public string? StandardOutput { get; init; }
    [JsonPropertyName("standard_error")]
    public string? StandardError { get; init; }
    [JsonPropertyName("classification")]
    public string? Classification { get; init; }
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
    [JsonPropertyName("job_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? JobId { get; init; }
    [JsonPropertyName("observation_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationId { get; init; }
    [JsonPropertyName("execution_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; init; }
    [JsonPropertyName("result_file"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultFile { get; init; }
    [JsonPropertyName("result_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TestArtifactFormat? ResultFormat { get; init; }
}
