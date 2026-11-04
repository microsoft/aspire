// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Authoritative, versioned collection facts transferred independently of the agent's proposal.</summary>
internal sealed record EvidenceBundle
{
    public required int SchemaVersion { get; init; }
    public required CollectedWorkflowRun Run { get; init; }
    public required CollectedSubjectPr? SubjectPr { get; init; }
    public required CollectedMainContext? MainContext { get; init; }
    public required ImmutableArray<JobEvidence> Jobs { get; init; }
    public required CollectedTestEvidence TestEvidence { get; init; }
    public required ImmutableArray<PriorCauseIdentity> PriorCauseIdentities { get; init; }
}

/// <summary>Transfers verified repository, run, attempt, revision and scope facts independently of the proposal.</summary>
internal sealed record CollectedWorkflowRun
{
    public required string Repository { get; init; }
    public required long Id { get; init; }
    public required int Attempt { get; init; }
    public required string HeadSha { get; init; }
    public required RunScope Scope { get; init; }
    public required string Event { get; init; }
    public required string HeadBranch { get; init; }
    public required string Url { get; init; }
}

/// <summary>Retains an established subject-PR number even when optional descriptive metadata is unavailable.</summary>
internal sealed record CollectedSubjectPr
{
    public required long Number { get; init; }
    public required CollectedPrMetadata? Metadata { get; init; }
}

/// <summary>Captures descriptive PR fields separately from its established subject identity.</summary>
internal sealed record CollectedPrMetadata
{
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string State { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string Url { get; init; }
}

/// <summary>Groups the failed main revision, successful baseline, triggering merge and candidate-history facts.</summary>
internal sealed record CollectedMainContext
{
    public required CollectedLastSuccess? LastSuccessfulRun { get; init; }
    public required string FailedSha { get; init; }
    public required CollectedCandidateHistory CandidateHistory { get; init; }
    public required CollectedTriggeringMerge? TriggeringMerge { get; init; }
}

/// <summary>Captures the most recent qualifying successful run used as the main-branch comparison baseline.</summary>
internal sealed record CollectedLastSuccess
{
    public required long Id { get; init; }
    public required string HeadSha { get; init; }
    public required string CreatedAt { get; init; }
    public required string? Url { get; init; }
}

/// <summary>Declares candidate-history availability and includes candidates only when the comparison is usable.</summary>
internal sealed record CollectedCandidateHistory
{
    public required CandidateHistoryState State { get; init; }
    public required ImmutableArray<CollectedCandidate>? Candidates { get; init; }
}

/// <summary>Captures one candidate commit and its verified merged-PR association.</summary>
internal sealed record CollectedCandidate
{
    public required string Sha { get; init; }
    public required string Message { get; init; }
    public required string Url { get; init; }
    public required CollectedCandidatePr PullRequest { get; init; }
}

/// <summary>Captures the merged PR associated with a candidate main-branch commit.</summary>
internal sealed record CollectedCandidatePr
{
    public required long Number { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string? MergedAt { get; init; }
}

/// <summary>Captures the triggering merge&apos;s PR identity, branch, author and merge-time context.</summary>
internal sealed record CollectedTriggeringMerge
{
    public required long Number { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string State { get; init; }
    public required string HeadBranch { get; init; }
    public required string BaseBranch { get; init; }
    public required string Url { get; init; }
    public required string? MergedAt { get; init; }
}

/// <summary>Captured upstream job facts used to validate an agent's job decisions.</summary>
internal sealed record JobEvidence
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunnerFailure { get; init; }
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string? Url { get; init; }
    public required string Conclusion { get; init; }
    public required ImmutableArray<StepEvidence> Steps { get; init; }
}

/// <summary>Preserves a source step's order, name and completion state for failure-phase diagnosis.</summary>
internal sealed record StepEvidence
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required string? Conclusion { get; init; }
}

/// <summary>Declares structured-result availability with the complete captured failure inventory and acquisition problems.</summary>
internal sealed record CollectedTestEvidence
{
    public required EvidenceState State { get; init; }
    public required ImmutableArray<CollectedTestFailure> Failures { get; init; }
    public required ImmutableArray<AnalysisProblem> Problems { get; init; }
}

/// <summary>Transfers an exact test identity, opaque reference, producing job, diagnostics and all result sources.</summary>
internal sealed record CollectedTestFailure
{
    public required string Ref { get; init; }
    public required long JobId { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required CollectedDiagnostics Diagnostics { get; init; }
    public required ImmutableArray<TestResultSource> Sources { get; init; }
}

/// <summary>Carries captured structured test error, stack and output text before publication projection.</summary>
internal sealed record CollectedDiagnostics
{
    public required string Error { get; init; }
    public required string StackTrace { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
}

/// <summary>Identifies the artifact, extracted result file, format and execution behind a test observation.</summary>
internal sealed record TestResultSource
{
    public required long ArtifactId { get; init; }
    public required string ResultFile { get; init; }
    public required TestArtifactFormat Format { get; init; }
    public required string? ExecutionId { get; init; }
}

/// <summary>Transfers a bounded problem message with the acquisition or finalization stage where it occurred.</summary>
internal sealed record AnalysisProblem
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
}

/// <summary>Transfers a known historical cause&apos;s identity and type for proposal-reference validation.</summary>
internal sealed record PriorCauseIdentity
{
    public required string Id { get; init; }
    public required CauseType Type { get; init; }
}

/// <summary>The agent supplies decisions and references, never copied evidence or publication metadata.</summary>
internal sealed record AnalysisProposal
{
    public required int SchemaVersion { get; init; }
    public required ProposedWorkflowRun Run { get; init; }
    public required ImmutableArray<ProposedJobDecision> Jobs { get; init; }
    public required ImmutableArray<ProposedTestDecision> Tests { get; init; }
    public required ImmutableArray<ProposedCause> Causes { get; init; }
}

/// <summary>Binds an agent proposal to the exact repository, run and attempt being analyzed.</summary>
internal sealed record ProposedWorkflowRun
{
    public required string Repository { get; init; }
    public required long Id { get; init; }
    public required int Attempt { get; init; }
}

/// <summary>Classifies one trusted job identifier for independent proposal validation.</summary>
internal sealed record ProposedJobDecision
{
    public required long JobId { get; init; }
    public required JobClassification Classification { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Classifies one opaque collected test reference without copying its identity or diagnostics.</summary>
internal sealed record ProposedTestDecision
{
    public required string Ref { get; init; }
    public required TestClassification Classification { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Defines the evidence claims and rationale common to new and existing agent cause proposals.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExistingCauseProposal), "existing")]
[JsonDerivedType(typeof(NewCauseProposal), "new")]
internal abstract record ProposedCause
{
    public required ImmutableArray<long> JobIds { get; init; }
    public required ImmutableArray<string> TestRefs { get; init; }
    public required string Reason { get; init; }
}

/// <summary>References a catalogued cause and claims the jobs and test references it explains.</summary>
internal sealed record ExistingCauseProposal : ProposedCause
{
    public required string Id { get; init; }
}

/// <summary>Proposes a new cause definition and claims the jobs and test references it explains.</summary>
internal sealed record NewCauseProposal : ProposedCause
{
    public required ProposedCauseDefinition Definition { get; init; }
}

/// <summary>Describes an agent-proposed new cause that must be validated before durable publication.</summary>
internal sealed record ProposedCauseDefinition
{
    public required string Id { get; init; }
    public required CauseType Type { get; init; }
    public required string Title { get; init; }
    public required string ErrorPattern { get; init; }
}
