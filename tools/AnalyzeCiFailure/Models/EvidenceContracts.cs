// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

// Identity tokens retain their JSON representation: 123, 123.0 and "123"
// are not interchangeable in every part of the existing evidence contract.
/// <summary>Retains captured run identity tokens and scope for compatible evidence and publication readers.</summary>
internal sealed record RunContext
{
    [JsonPropertyName("run_id")]
    public JsonElement? RunId { get; init; }
    [JsonPropertyName("run_attempt")]
    public JsonElement? RunAttempt { get; init; }
    [JsonPropertyName("run_scope")]
    public RunScope? RunScope { get; init; }
    [JsonPropertyName("head_sha")]
    public JsonElement? HeadSha { get; init; }
    [JsonPropertyName("event"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Event { get; init; }
    [JsonPropertyName("head_branch"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HeadBranch { get; init; }
    [JsonPropertyName("pr_numbers"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? PrNumbers { get; init; }
}

/// <summary>Transfers selected artifact identity, producing job and parser metadata for evidence reconstruction.</summary>
internal sealed record TestResultArtifactRecord
{
    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("size_in_bytes")]
    public JsonElement? SizeInBytes { get; init; }
    [JsonPropertyName("job")]
    public string? Job { get; init; }
    [JsonPropertyName("format")]
    public TestArtifactFormat? Format { get; init; }
}

// These are read-only views of captured GitHub payloads. Unknown upstream fields
// are intentionally ignored; these partial contracts never overwrite the capture.
/// <summary>Reads the captured run URL and revision used by compatible evidence consumers.</summary>
internal sealed record WorkflowRun
{
    [JsonPropertyName("html_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HtmlUrl { get; init; }
    [JsonPropertyName("head_sha")]
    public JsonElement? HeadSha { get; init; }
}

/// <summary>Reads captured job identity, status and steps while retaining their upstream JSON shapes.</summary>
internal sealed record WorkflowJob
{
    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }
    [JsonPropertyName("name"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Name { get; init; }
    [JsonPropertyName("conclusion")]
    public JsonElement? Conclusion { get; init; }
    [JsonPropertyName("html_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HtmlUrl { get; init; }
    [JsonPropertyName("steps")]
    public JsonElement? Steps { get; init; }
}

/// <summary>Reads a captured workflow step&apos;s name and conclusion without requiring unrelated upstream fields.</summary>
internal sealed record WorkflowStep
{
    [JsonPropertyName("name"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Name { get; init; }
    [JsonPropertyName("conclusion"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Conclusion { get; init; }
}

/// <summary>Carries optional captured PR fields in the compatible shape used by publication.</summary>
internal sealed record PullRequestMetadata
{
    [JsonPropertyName("number")]
    public JsonElement? Number { get; init; }
    [JsonPropertyName("title"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Title { get; init; }
    [JsonPropertyName("user"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? User { get; init; }
    [JsonPropertyName("state"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? State { get; init; }
    [JsonPropertyName("head_branch"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HeadBranch { get; init; }
    [JsonPropertyName("base_branch"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? BaseBranch { get; init; }
    [JsonPropertyName("html_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HtmlUrl { get; init; }
}

/// <summary>Reads merge, branch and author context from a captured GitHub pull request.</summary>
internal sealed record MergedPullRequest
{
    [JsonPropertyName("number")]
    public JsonElement? Number { get; init; }
    [JsonPropertyName("title"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Title { get; init; }
    [JsonPropertyName("user"), JsonConverter(typeof(CiFailureJson.OptionalObjectConverter<GitHubUser>))]
    public GitHubUser? User { get; init; }
    [JsonPropertyName("state"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? State { get; init; }
    [JsonPropertyName("head"), JsonConverter(typeof(CiFailureJson.OptionalObjectConverter<GitHubBranch>))]
    public GitHubBranch? Head { get; init; }
    [JsonPropertyName("base"), JsonConverter(typeof(CiFailureJson.OptionalObjectConverter<GitHubBranch>))]
    public GitHubBranch? Base { get; init; }
    [JsonPropertyName("html_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HtmlUrl { get; init; }
    [JsonPropertyName("merged_at")]
    public JsonElement? MergedAt { get; init; }
}

/// <summary>Reads the author login needed from optional GitHub user metadata.</summary>
internal sealed record GitHubUser
{
    [JsonPropertyName("login"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Login { get; init; }
}

/// <summary>Reads the branch reference needed from optional GitHub PR metadata.</summary>
internal sealed record GitHubBranch
{
    [JsonPropertyName("ref"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Ref { get; init; }
}

/// <summary>Reads a candidate commit and its associated merged PR without rewriting the captured payload.</summary>
internal sealed record CandidateMerge
{
    [JsonPropertyName("sha")]
    public JsonElement? Sha { get; init; }
    [JsonPropertyName("message"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Message { get; init; }
    [JsonPropertyName("html_url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? HtmlUrl { get; init; }
    [JsonPropertyName("pull_request"), JsonConverter(typeof(CiFailureJson.OptionalObjectConverter<CandidatePullRequest>))]
    public CandidatePullRequest? PullRequest { get; init; }
}

/// <summary>Reads the PR identity and merge context attached to a candidate commit.</summary>
internal sealed record CandidatePullRequest
{
    [JsonPropertyName("number")]
    public JsonElement? Number { get; init; }
    [JsonPropertyName("title"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Title { get; init; }
    [JsonPropertyName("url"), JsonConverter(typeof(CiFailureJson.OptionalTextConverter))]
    public string? Url { get; init; }
    [JsonPropertyName("merged_at")]
    public JsonElement? MergedAt { get; init; }
}

/// <summary>Transfers the availability state of captured main-branch merge history.</summary>
internal sealed record CandidateHistoryStatus
{
    [JsonPropertyName("state")]
    public CandidateHistoryState? State { get; init; }
}

/// <summary>Groups the compatible run context and captured workflow facts consumed by validation and rendering.</summary>
internal sealed record RunEvidence(RunContext Context, WorkflowRun Metadata, ImmutableArray<WorkflowJob> Jobs);

// Existing durable/renderer projection. Transient evidence uses CollectedTestFailure.
/// <summary>Carries publication-compatible test diagnostics and precise observation and source identities.</summary>
internal sealed record TestFailureEvidence
{
    [JsonPropertyName("test")]
    public required string Name { get; init; }
    [JsonPropertyName("display_test")]
    public required string DisplayName { get; init; }
    [JsonPropertyName("job")]
    public required string Job { get; init; }
    [JsonPropertyName("job_id")]
    public JsonElement? JobId { get; init; }
    [JsonPropertyName("result_file")]
    public string? ResultFile { get; init; }
    [JsonPropertyName("result_format")]
    public TestArtifactFormat? ResultFormat { get; init; }
    [JsonPropertyName("execution_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; init; }
    [JsonPropertyName("error")]
    public required string Error { get; init; }
    [JsonPropertyName("stack_trace")]
    public required string StackTrace { get; init; }
    [JsonPropertyName("standard_output")]
    public required string StandardOutput { get; init; }
    [JsonPropertyName("standard_error")]
    public required string StandardError { get; init; }
    [JsonPropertyName("observation_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationId { get; init; }
}

/// <summary>Stores a cause occurrence&apos;s test identity, bounded diagnostics and result provenance.</summary>
internal sealed record OccurrenceTest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("job_id")]
    public JsonElement? JobId { get; init; }
    [JsonPropertyName("job")]
    public required string Job { get; init; }
    [JsonPropertyName("error")]
    public required string Error { get; init; }
    [JsonPropertyName("stack_trace")]
    public required string StackTrace { get; init; }
    [JsonPropertyName("standard_output")]
    public required string StandardOutput { get; init; }
    [JsonPropertyName("standard_error")]
    public required string StandardError { get; init; }
    [JsonPropertyName("result_file")]
    public string? ResultFile { get; init; }
    [JsonPropertyName("result_format")]
    public TestArtifactFormat? ResultFormat { get; init; }
    [JsonPropertyName("observation_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationId { get; init; }
    [JsonPropertyName("execution_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; init; }
}

/// <summary>Records one run&apos;s observed occurrence of a cause with job, PR and test context.</summary>
internal sealed record CauseOccurrence
{
    [JsonPropertyName("run_id")]
    public JsonElement? RunId { get; init; }
    [JsonPropertyName("run_url")]
    public required string RunUrl { get; init; }
    [JsonPropertyName("job")]
    public required string Job { get; init; }
    [JsonPropertyName("observed_at")]
    public required string ObservedAt { get; init; }
    [JsonPropertyName("pr_number")]
    public JsonElement? PrNumber { get; init; }
    [JsonPropertyName("tests"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<OccurrenceTest>? Tests { get; init; }
}

/// <summary>Projects captured PR and main-history facts into the compatible views used by publication policy.</summary>
internal sealed record PublicationEvidence(
    PullRequestMetadata? PullRequest,
    WorkflowRun? LastSuccess,
    MergedPullRequest? TriggeringMerge,
    CandidateHistoryState CandidateState,
    ImmutableArray<CandidateMerge>? Candidates)
{
    internal static PublicationEvidence FromBundle(EvidenceBundle bundle)
    {
        static JsonElement Text(string text) => JsonSerializer.SerializeToElement(text);
        static JsonElement Id(long id) => JsonSerializer.SerializeToElement(id);
        var metadata = bundle.SubjectPr?.Metadata;
        var main = bundle.MainContext;
        var merge = main?.TriggeringMerge;
        return new PublicationEvidence(
            metadata is null ? null : new PullRequestMetadata
            {
                Number = Id(bundle.SubjectPr!.Number),
                Title = metadata.Title,
                User = metadata.Author,
                State = metadata.State,
                HeadBranch = metadata.HeadBranch,
                BaseBranch = metadata.BaseBranch,
                HtmlUrl = metadata.Url,
            },
            main?.LastSuccessfulRun is not { } last ? null : new WorkflowRun
            {
                HeadSha = Text(last.HeadSha),
                HtmlUrl = last.Url,
            },
            merge is null ? null : new MergedPullRequest
            {
                Number = Id(merge.Number),
                Title = merge.Title,
                User = new GitHubUser { Login = merge.Author },
                State = merge.State,
                Head = new GitHubBranch { Ref = merge.HeadBranch },
                Base = new GitHubBranch { Ref = merge.BaseBranch },
                HtmlUrl = merge.Url,
                MergedAt = merge.MergedAt is null ? null : Text(merge.MergedAt),
            },
            main?.CandidateHistory.State ?? CandidateHistoryState.Unavailable,
            main?.CandidateHistory.State != CandidateHistoryState.Available ? null :
                main.CandidateHistory.Candidates?.Select(candidate => new CandidateMerge
                {
                    Sha = Text(candidate.Sha),
                    Message = candidate.Message,
                    HtmlUrl = candidate.Url,
                    PullRequest = new CandidatePullRequest
                    {
                        Number = Id(candidate.PullRequest.Number),
                        Title = candidate.PullRequest.Title,
                        Url = candidate.PullRequest.Url,
                        MergedAt = candidate.PullRequest.MergedAt is null ? null : Text(candidate.PullRequest.MergedAt),
                    },
                }).ToImmutableArray());
    }

}
