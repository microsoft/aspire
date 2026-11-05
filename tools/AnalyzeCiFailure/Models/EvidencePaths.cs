// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

/// <summary>Names evidence-package files and identifies captured JSON used to build diagnosis context.</summary>
internal sealed class EvidencePaths(string rootDirectory)
{
    internal const string JobLogPattern = "job-*.log";
    internal const string JobAnnotationsPattern = "annotations-*.json";

    public string RootDirectory { get; } = rootDirectory;
    public string Bundle { get; } = Path.Combine(rootDirectory, "evidence.json");
    public string Outcome { get; } = Path.Combine(rootDirectory, "collection-outcome.json");
    public string Run { get; } = Path.Combine(rootDirectory, "diagnostics", "run.json");
    public string AllJobs { get; } = Path.Combine(rootDirectory, "diagnostics", "all-jobs.json");
    public string FailedJobs { get; } = Path.Combine(rootDirectory, "diagnostics", "failed-jobs.json");
    public string RetryPatterns { get; } = Path.Combine(rootDirectory, "diagnostics", "retry-patterns.json");
    public string TriggeringMerge { get; } = Path.Combine(rootDirectory, "diagnostics", "triggering-merge-pr.json");
    public string LastSuccessfulMainRun { get; } = Path.Combine(rootDirectory, "diagnostics", "last-successful-main-run.json");
    public string CandidateMerges { get; } = Path.Combine(rootDirectory, "diagnostics", "candidate-merges.json");
    public string CandidateHistoryStatus { get; } = Path.Combine(rootDirectory, "diagnostics", "candidate-merge-history-status.json");
    public string PullRequestFiles { get; } = Path.Combine(rootDirectory, "diagnostics", "pr-files.json");
    public string PullRequestMetadata { get; } = Path.Combine(rootDirectory, "diagnostics", "pr-metadata.json");
    public string Artifacts { get; } = Path.Combine(rootDirectory, "diagnostics", "artifacts.json");
    public string SelectedTestArtifacts { get; } = Path.Combine(rootDirectory, "diagnostics", "selected-test-result-artifacts.json");
    public string PriorCauseIndex { get; } = Path.Combine(rootDirectory, "history", "cause-index.json");
    public string Summary { get; } = Path.Combine(rootDirectory, "analysis-summary.md");
    public string PriorCausesDirectory { get; } = Path.Combine(rootDirectory, "history", "causes");
    public string Manifest { get; } = Path.Combine(rootDirectory, "manifest.json");

    public string JobLog(string jobId) => Path.Combine(RootDirectory, "diagnostics", $"job-{jobId}.log");
    public string JobAnnotations(string jobId) => Path.Combine(RootDirectory, "diagnostics", $"annotations-{jobId}.json");
    public string PriorCause(string fileName) => Path.Combine(PriorCausesDirectory, fileName);    internal static bool IsDiagnosisContextJson(string name)
        => name is "diagnostics/run.json" or "diagnostics/failed-jobs.json" or "diagnostics/pr-files.json" or "diagnostics/retry-patterns.json" ||
            name.StartsWith("diagnostics/annotations-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);

}
