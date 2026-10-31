// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Maps captured GitHub payloads to typed evidence records without fetching data or classifying failures.</summary>
internal static class CollectionProjection
{
    internal static ImmutableArray<JobEvidence> ProjectJobs(JsonArray jobs) => jobs.Select(job => new JobEvidence
    {
        Id = GitHubJson.ReadRequiredId(job!["id"]),
        Name = Text(job["name"]) ?? throw new ValidationException("A collected job needs a name"),
        Url = Text(job["html_url"]),
        Conclusion = Text(job["conclusion"]) ?? "",
        Steps = (job["steps"] as JsonArray ?? []).Select(step => new StepEvidence
        {
            Number = step?["number"]?.GetValue<int>() ?? throw new ValidationException("Collected job steps must provide their source number."),
            Name = Text(step?["name"]) ?? "",
            Conclusion = Text(step?["conclusion"]),
        }).ToImmutableArray(),
    }).ToImmutableArray();

    internal static CollectedPrMetadata ProjectPullRequest(JsonNode metadata) => new()
    {
        Title = Text(metadata["title"]) ?? "", Author = Text(Field(metadata["user"], "login")) ?? "",
        State = Text(metadata["state"]) ?? "", HeadBranch = Text(Field(metadata["head"], "ref")) ?? "",
        BaseBranch = Text(Field(metadata["base"], "ref")) ?? "", Url = Text(metadata["html_url"]) ?? "",
    };

    internal static CollectedMainContext ProjectMainContext(
        string headSha,
        JsonObject last,
        JsonObject merge,
        ImmutableArray<CandidateMerge> candidates,
        CandidateHistoryState state) => new()
    {
        FailedSha = headSha,
        LastSuccessfulRun = last.Count == 0 ? null : new CollectedLastSuccess
        {
            Id = GitHubJson.ReadRequiredId(last["id"]), HeadSha = Text(last["head_sha"]) ?? "",
            CreatedAt = Text(last["created_at"]) ?? "", Url = Text(last["html_url"]),
        },
        TriggeringMerge = merge.Count == 0 ? null : new CollectedTriggeringMerge
        {
            Number = GitHubJson.ReadRequiredId(merge["number"]), Title = Text(merge["title"]) ?? "",
            Author = Text(Field(merge["user"], "login")) ?? "", State = Text(merge["state"]) ?? "",
            HeadBranch = Text(Field(merge["head"], "ref")) ?? "", BaseBranch = Text(Field(merge["base"], "ref")) ?? "",
            Url = Text(merge["html_url"]) ?? "", MergedAt = Text(merge["merged_at"]),
        },
        CandidateHistory = new CollectedCandidateHistory
        {
            State = state,
            Candidates = state != CandidateHistoryState.Available ? null : candidates.Select(candidate => new CollectedCandidate
            {
                Sha = AsString(candidate.Sha) ?? "",
                Message = candidate.Message ?? "",
                Url = candidate.HtmlUrl ?? "",
                PullRequest = candidate.PullRequest is not { } pr ? throw new ValidationException("A candidate merge needs a pull request.") : new CollectedCandidatePr
                {
                    Number = GitHubJson.ReadRequiredId(pr.Number),
                    Title = pr.Title ?? "",
                    Url = pr.Url ?? "",
                    MergedAt = AsString(pr.MergedAt),
                },
            }).ToImmutableArray(),
        },
    };

    internal static EvidenceBundle CreateBundle(
        string repository,
        AnalysisProgress progress,
        JsonNode run,
        ImmutableArray<JobEvidence> jobs,
        ImmutableArray<CollectedTestFailure> failures,
        EvidenceState state,
        ImmutableArray<PriorCauseIndexEntry> prior,
        CollectedPrMetadata? prMetadata,
        CollectedMainContext? mainContext) => new()
    {
        SchemaVersion = 1,
        Run = new CollectedWorkflowRun
        {
            Repository = repository, Id = progress.RunId!.Value, Attempt = progress.RunAttempt!.Value,
            HeadSha = Text(run["head_sha"]) ?? "", Scope = progress.RunScope!.Value,
            Event = Text(run["event"]) ?? "", HeadBranch = Text(run["head_branch"]) ?? "",
            Url = Text(run["html_url"]) ?? "",
        },
        SubjectPr = progress.SubjectPrNumber is not { } number ? null : new CollectedSubjectPr { Number = number, Metadata = prMetadata },
        MainContext = mainContext,
        Jobs = jobs,
        TestEvidence = new CollectedTestEvidence
        {
            State = state, Failures = failures,
            Problems = progress.Problems.Where(problem => problem.Category == AnalysisProblemCategory.TestEvidence)
                .Select(problem => problem.Problem).ToImmutableArray(),
        },
        PriorCauseIdentities = prior.Where(cause => cause.ReadError is null && cause.Id is not null && cause.Type is not null)
            .Select(cause => new PriorCauseIdentity { Id = cause.Id!, Type = cause.Type!.Value }).ToImmutableArray(),
    };
}
