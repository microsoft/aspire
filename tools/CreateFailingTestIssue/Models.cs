// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace CreateFailingTestIssue;

public sealed record CommandInput(
    string? TestQuery,
    string? SourceUrl,
    string WorkflowSelector,
    string Repository,
    bool ForceNew,
    bool Create);

public sealed record CreateFailingTestIssueResult
{
    public required bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public required InputSection Input { get; init; }

    public ResolutionSection? Resolution { get; init; }

    public MatchSection? Match { get; init; }

    public AllFailuresSection? AllFailures { get; init; }

    public FailureSection? Failure { get; init; }

    public MatrixSection? Matrix { get; init; }

    public IssueSection? Issue { get; init; }

    public required DiagnosticsSection Diagnostics { get; init; }
}

public sealed record InputSection(
    string? TestQuery,
    string? SourceUrl,
    WorkflowSection Workflow,
    bool ForceNew);

public sealed record WorkflowSection(
    string Requested,
    string ResolvedWorkflowFile,
    string ResolvedWorkflowName);

public sealed record ResolutionSection(
    string SourceKind,
    long RunId,
    string RunUrl,
    int RunAttempt,
    int? PullRequestNumber,
    IReadOnlyList<string> JobUrls);

public sealed record MatchSection(
    string Query,
    string Strategy,
    string CanonicalTestName,
    string DisplayTestName,
    IReadOnlyList<MatchedOccurrence> AllMatchingOccurrences);

public sealed record AllFailuresSection(
    int FailedTests,
    IReadOnlyList<FailingTestEntry> Tests);

public sealed record FailingTestEntry(
    string CanonicalTestName,
    string DisplayTestName,
    int OccurrenceCount,
    IReadOnlyList<MatchedOccurrence> Occurrences,
    FailureSection PrimaryFailure);

public sealed record MatchedOccurrence(
    string CanonicalTestName,
    string DisplayTestName,
    string ArtifactName,
    string? ArtifactUrl,
    string TrxPath,
    string? JobName,
    string? JobUrl);

public sealed record FailureSection(
    string ErrorMessage,
    string StackTrace,
    string Stdout,
    string ArtifactName,
    string? ArtifactUrl,
    string TrxPath,
    string? JobName,
    string? JobUrl);

public sealed record MatrixSection(
    int MatchedOccurrences,
    IReadOnlyList<MatrixSummaryEntry> Summary);

public sealed record MatrixSummaryEntry(
    string ArtifactName,
    string? ArtifactUrl,
    string TrxPath,
    string? JobName,
    string? JobUrl);

public sealed record IssueSection(
    string Title,
    IReadOnlyList<string> Labels,
    string StableSignature,
    string MetadataMarker,
    string Body,
    string CommentBody,
    ExistingIssueInfo? ExistingIssue,
    CreatedIssueInfo? CreatedIssue);

public sealed record CreatedIssueInfo(
    int Number,
    string Url);

public sealed record ExistingIssueInfo(
    int Number,
    string Url);

public sealed record DiagnosticsSection(
    [property: JsonIgnore] IReadOnlyList<string> Log,
    string LogFile,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AvailableFailedTests);

public sealed record WorkflowSelectorResolution(
    bool Success,
    string Requested,
    string? ResolvedWorkflowFile,
    string? ErrorMessage);

public sealed record SourceUrlResolution(
    bool Success,
    string? SourceKind,
    int? PullRequestNumber,
    long? RunId,
    int? RunAttempt,
    long? JobId,
    string? ErrorMessage);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TruncationPreference
{
    Start,
    Middle,
    End
}

public sealed record TruncationResult(
    string Content,
    bool WasTruncated,
    string? Note);

public sealed record FailedTestOccurrence(
    string CanonicalTestName,
    string DisplayTestName,
    string ArtifactName,
    string TrxPath,
    string ErrorMessage,
    string StackTrace,
    string Stdout,
    string? ArtifactUrl = null,
    string? JobName = null,
    string? JobUrl = null);

public sealed record FailureMatchResult(
    bool Success,
    string? ErrorMessage,
    string? Strategy,
    string? CanonicalTestName,
    string? DisplayTestName,
    IReadOnlyList<FailedTestOccurrence> Occurrences,
    IReadOnlyList<string> CandidateNames);

public sealed record IssueArtifacts(
    string Title,
    IReadOnlyList<string> Labels,
    string StableSignature,
    string MetadataMarker,
    string Body,
    string CommentBody);
