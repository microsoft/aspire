// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Builds durable per-run history records from validated decisions and captured run facts.</summary>
internal static class RunSummaryBuilder
{
    internal static RunSummaryRecord Create(
        AnalysisReport analysis,
        RunEvidence evidence,
        PublicationEvidence publication,
        string observedAt)
    {
        var context = evidence.Context;
        var main = context.RunScope == RunScope.Main;
        var classifications = analysis.Jobs.GroupBy(j => AsNormalizedNumber(j.Id))
            .ToDictionary(g => g.Key!.Value, g => g.Last());
        var jobs = evidence.Jobs.Select(job => job with { Name = DiagnosticTextSanitizer.Sanitize(job.Name!, limit: 500) }).ToArray();
        var names = jobs.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);
        var pr = publication.PullRequest;
        var triggering = publication.TriggeringMerge;

        return new RunSummaryRecord
        {
            RunId = context.RunId,
            RunAttempt = context.RunAttempt,
            RunUrl = DisplayText(evidence.Metadata.HtmlUrl, limit: 1000),
            RunScope = context.RunScope,
            AnalyzedAt = observedAt,
            Verdict = analysis.Verdict,
            PullRequest = !main && context.RunScope == RunScope.PullRequest && AsNormalizedNumber(pr?.Number) is not null
                ? new PullRequestSummary
                {
                    Number = pr!.Number,
                    Title = DisplayText(pr.Title, limit: 500),
                    Author = DisplayText(pr.User, limit: 100),
                    State = DisplayText(pr.State, limit: 50),
                    HeadBranch = DisplayText(pr.HeadBranch, limit: 500),
                    BaseBranch = DisplayText(pr.BaseBranch, limit: 500),
                    Url = DisplayText(pr.HtmlUrl, limit: 1000),
                } : null,
            TriggeringMerge = main && publication.CandidateState == CandidateHistoryState.Available && AsNormalizedNumber(triggering?.Number) is not null
                ? new TriggeringMergeSummary
                {
                    Number = triggering!.Number,
                    Title = DisplayText(triggering.Title, limit: 500),
                    Author = DisplayText(triggering.User?.Login, limit: 100),
                    State = DisplayText(triggering.State, limit: 50),
                    HeadBranch = DisplayText(triggering.Head?.Ref, limit: 500),
                    BaseBranch = DisplayText(triggering.Base?.Ref, limit: 500),
                    Url = DisplayText(triggering.HtmlUrl, limit: 1000),
                    MergedAt = triggering.MergedAt,
                } : null,
            MainContext = main ? new MainSummary
            {
                LastSuccessfulSha = publication.LastSuccess?.HeadSha,
                FailedSha = context.HeadSha,
                CandidateState = publication.CandidateState,
                Candidates = publication.Candidates?.Select(candidate => candidate with
                {
                    Message = DisplayText(candidate.Message, limit: 500),
                    HtmlUrl = DisplayText(candidate.HtmlUrl, limit: 1000),
                    PullRequest = new CandidatePullRequest
                    {
                        Number = candidate.PullRequest?.Number,
                        Title = DisplayText(candidate.PullRequest?.Title, limit: 500),
                        Url = DisplayText(candidate.PullRequest?.Url, limit: 1000),
                        MergedAt = candidate.PullRequest?.MergedAt,
                    },
                }).ToArray(),
            } : null,
            Jobs = jobs.Select(job =>
            {
                classifications.TryGetValue(AsNormalizedNumber(job.Id)!.Value, out var classification);

                return new SummaryJob
                {
                    Name = job.Name,
                    Id = job.Id,
                    Conclusion = job.Conclusion,
                    Url = DisplayText(job.HtmlUrl, limit: 1000),
                    Classification = classification?.Classification,
                    Reason = classification?.Reason ?? "",
                    FailedSteps = Steps(job.Steps)
                        .Where(step => step.Conclusion is "failure" or "cancelled" or "timed_out")
                        .Select(step => DisplayText(step.Name, limit: 500)).ToArray(),
                };
            }).ToArray(),
            Tests = analysis.Tests.Select(test => new SummaryTest
            {
                Name = DiagnosticTextSanitizer.Sanitize(test.DisplayName ?? test.Name ?? "", limit: 500),
                Job = names.Contains(test.Job) ? test.Job : "",
                Error = test.Error ?? "",
                StackTrace = test.StackTrace ?? "",
                StandardOutput = test.StandardOutput ?? "",
                StandardError = test.StandardError ?? "",
                Classification = test.Classification is { } classification ? EnumText(classification) : "",
                Reason = test.Reason ?? "",
                JobId = AsNormalizedNumber(test.JobId) is not null ? test.JobId : null,
                ObservationId = test.ObservationId,
                ExecutionId = test.ExecutionId,
                ResultFile = test.ResultFile,
                ResultFormat = test.ResultFormat,
            }).ToArray(),
            Causes = analysis.Causes.ToArray(),
        };
    }

    private static IEnumerable<WorkflowStep> Steps(JsonElement? value)
        // GitHub can omit steps for interrupted jobs. Non-array diagnostics and
        // non-object entries were never treated as failed steps.
        => value is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(step => step.ValueKind == JsonValueKind.Object)
                .Select(step => CiFailureJson.ToContract<WorkflowStep>(step)!)
            : [];

    private static string DisplayText(string? value, int limit)
        => DiagnosticTextSanitizer.SanitizeRunMetadata(value ?? "", limit);
}
