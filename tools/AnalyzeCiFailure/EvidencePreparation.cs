// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Prepares collected and replayed evidence, retaining established facts and failure diagnostics.</summary>
internal static class EvidencePreparation
{
    internal static async Task<int> CollectAsync(
        string workingDirectory,
        string repository,
        AnalysisInvocation invocation,
        string? runIdText,
        string? runAttemptText,
        string? githubOutput,
        Func<GitHubApiClient> createGitHubClient,
        CancellationToken ct)
    {
        AnalysisOutcome outcome;
        try
        {
            using var github = createGitHubClient();
            outcome = await CollectEvidenceAndPrepareAnalysisAsync(
                workingDirectory,
                repository,
                invocation,
                runIdText,
                runAttemptText,
                github,
                ct);
        }
        catch (Exception error)
        {
            if (invocation.Capture.Failure is null)
            {
                invocation.Capture.Failure = error;
                invocation.Progress.RecordProblem(AnalysisProblemCategory.Fatal, invocation.Progress.Stage, error.Message);
                EvidencePackageStore.InvalidateEvidenceViews(invocation.Evidence, invocation.Progress);
                await invocation.WriteRecoveryBestEffortAsync("collection", "failed");
                try
                {
                    Directory.CreateDirectory(invocation.Evidence.RootDirectory);
                    await EvidencePackageStore.WriteOutcomeAsync(invocation.Evidence, invocation.Progress.CreateOutcome("failed"));
                }
                catch (Exception outcomeError) when (outcomeError is IOException or UnauthorizedAccessException)
                {
                    invocation.Progress.RecordProblem(AnalysisProblemCategory.Fatal, "outcome", outcomeError.Message);
                    Console.Error.WriteLine($"::error::Unable to write collection outcome: {CiFailureJson.Display(outcomeError.Message)}");
                }
                await invocation.WriteDiagnosticsBestEffortAsync("failed");
            }
            Console.Error.WriteLine($"::error::{AnalysisDiagnostics.DescribeStage(invocation.Progress.Stage)} failed: " +
                CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(error.Message, limit: 1000)));
            outcome = invocation.Progress.CreateOutcome("failed");
        }
        return await CompleteCollectionAsync(invocation, outcome, githubOutput, ct);
    }


    internal static async Task<AnalysisOutcome> CollectEvidenceAndPrepareAnalysisAsync(
        string workingDirectory,
        string repository,
        AnalysisInvocation invocation,
        string? runIdText,
        string? runAttemptText,
        GitHubApiClient github,
        CancellationToken ct)
    {
        var scratch = invocation.Scratch;
        var capture = invocation.Capture;
        var progress = invocation.Progress;
        var evidencePaths = invocation.Evidence;
        try
        {
            Directory.CreateDirectory(evidencePaths.RootDirectory);
            var collector = new WorkflowRunEvidenceCollector(
                workingDirectory,
                repository,
                scratch,
                capture,
                new AspireCiWorkflowConventions(),
                commandEnvironment: null,
                github);
            var runId = TransientJson.ParseId(runIdText ?? "");

            int? runAttempt = null;
            if (runAttemptText is not null)
            {
                if (!int.TryParse(runAttemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt <= 0)
                {
                    throw new ValidationException("Run attempt must be a positive integer");
                }
                runAttempt = attempt;
            }

            // Acquire evidence first; a legitimate no-work result needs no diagnostic views.
            var result = await collector.CollectAsync(new(repository, runId, runAttempt), progress, ct);
            if (!result.HasRelevantFailures)
            {
                var noWork = progress.CreateOutcome("no-work");
                await EvidencePackageStore.WriteOutcomeAsync(evidencePaths, noWork);
                await invocation.WriteDiagnosticsAsync("no-work");
                ct.ThrowIfCancellationRequested();
                return noWork;
            }

            // Publish the evidence and agent views before declaring this invocation ready.
            progress.Stage = "views";
            var input = WorkflowFailureDiagnosisContextBuilder.Build(result.Run!);
            ct.ThrowIfCancellationRequested();
            await EvidencePackageStore.WriteDiagnosisAsync(evidencePaths, input, ct);

            var degraded = input.Evidence.TestEvidence.State == EvidenceState.Unavailable;
            var outcome = progress.CreateOutcome(degraded ? "degraded" : "ready");
            CiFailureJson.Write(
                evidencePaths.Manifest,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["history_commit"] = capture.HistoryCommit,
                    ["run_id"] = input.Evidence.Run.Id.ToString(CultureInfo.InvariantCulture),
                    ["run_attempt"] = input.Evidence.Run.Attempt,
                });
            if (progress.Problems.Count > 0)
            {
                await invocation.WriteRecoveryBestEffortAsync("collection", outcome.Status);
            }
            outcome = progress.CreateOutcome(outcome.Status);
            await EvidencePackageStore.WriteOutcomeAsync(evidencePaths, outcome);
            await invocation.WriteDiagnosticsAsync(outcome.Status);
            ct.ThrowIfCancellationRequested();
            return outcome;
        }
        catch (Exception ex)
        {
            // Preserve known progress, but invalidate any evidence left by a failed write.
            capture.Failure = ex;
            progress.RecordProblem(AnalysisProblemCategory.Fatal, progress.Stage, ex.Message);
            EvidencePackageStore.InvalidateEvidenceViews(evidencePaths, progress);
            await invocation.WriteRecoveryBestEffortAsync("collection", "failed");
            try
            {
                await EvidencePackageStore.WriteOutcomeAsync(evidencePaths, progress.CreateOutcome("failed"));
            }
            catch (Exception outcomeError) when (outcomeError is IOException or UnauthorizedAccessException)
            {
                progress.RecordProblem(AnalysisProblemCategory.Fatal, "outcome", outcomeError.Message);
                Console.Error.WriteLine($"::error::Unable to write collection outcome: {CiFailureJson.Display(outcomeError.Message)}");
            }
            await invocation.WriteDiagnosticsBestEffortAsync("failed");

            throw;
        }
    }

    internal static async Task<int> ReplayAsync(EvidencePaths paths, AnalysisInvocation invocation, CancellationToken ct)
    {
        var progress = invocation.Progress;
        var output = invocation.Replay;
        progress.Stage = "replay";
        try
        {
            Directory.CreateDirectory(output.RootDirectory);
            var bundle = TransientJson.ReadUtf8<EvidenceBundle>(await File.ReadAllBytesAsync(paths.Bundle, ct));
            progress.RunId = bundle.Run.Id; progress.RunAttempt = bundle.Run.Attempt; progress.HasRelevantFailures = true;
            progress.HeadSha = bundle.Run.HeadSha; progress.RunUrl = bundle.Run.Url; progress.RunScope = bundle.Run.Scope;
            progress.SubjectPrNumber = bundle.SubjectPr?.Number;
            AnalysisValidator.ValidateBundle(bundle, bundle.Run.Repository);
            var attachments = ImmutableDictionary.CreateBuilder<string, CollectedAttachment>(StringComparer.Ordinal);
            var jsonContext = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
            var files = (Directory.Exists(paths.PriorCausesDirectory) ? Directory.GetFiles(paths.PriorCausesDirectory, "*.json") : [])
                .Concat(Directory.Exists(Path.Combine(paths.RootDirectory, "diagnostics"))
                    ? Directory.GetFiles(Path.Combine(paths.RootDirectory, "diagnostics")) : []);
            foreach (var path in files)
            {
                var name = Path.GetRelativePath(paths.RootDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
                var attachment = new CollectedAttachment((await File.ReadAllBytesAsync(path, ct)).ToImmutableArray());
                if (EvidencePaths.IsDiagnosisContextJson(name))
                {
                    try
                    {
                        jsonContext.Add(name, JsonSerializer.SerializeToElement(CiFailureJson.Parse(attachment.Text)));
                        continue;
                    }
                    catch (JsonException error)
                    {
                        progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "attachments", error.Message);
                    }
                }
                if (name.StartsWith("history/causes/", StringComparison.Ordinal) ||
                    name.StartsWith("diagnostics/job-", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal) ||
                    name == "diagnostics/retry-patterns.json")
                {
                    attachments.Add(name, attachment);
                }
            }
            var captured = attachments.ToImmutable();
            var input = WorkflowFailureDiagnosisContextBuilder.Build(new CollectedRun(
                bundle,
                captured,
                PriorCauseCatalogue.BuildIndex(captured, bundle.TestEvidence.Failures))
            {
                JsonContext = jsonContext.ToImmutable(),
            });
            await EvidencePackageStore.WriteDiagnosisAsync(output, input, ct);
            var degraded = bundle.TestEvidence.State == EvidenceState.Unavailable;
            await EvidencePackageStore.WriteOutcomeAsync(output, progress.CreateOutcome(degraded ? "degraded" : "ready"));
            await invocation.WriteDiagnosticsAsync(degraded ? "degraded" : "ready");
            return 0;
        }
        catch (Exception ex)
        {
            invocation.Capture.Failure = ex;
            progress.RecordProblem(AnalysisProblemCategory.Fatal, "preparation", ex.Message);
            EvidencePackageStore.InvalidateEvidenceViews(output, progress);
            await invocation.WriteRecoveryBestEffortAsync("replay", "failed");
            try
            {
                await EvidencePackageStore.WriteOutcomeAsync(output, progress.CreateOutcome("failed"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                progress.RecordProblem(AnalysisProblemCategory.Fatal, "outcome", error.Message);
                Console.Error.WriteLine($"::error::Unable to write collection outcome: {CiFailureJson.Display(error.Message)}");
            }
            await invocation.WriteDiagnosticsBestEffortAsync("failed");
            throw;
        }
    }

    internal static async Task<int> CompleteCollectionAsync(
        AnalysisInvocation invocation,
        AnalysisOutcome outcome,
        string? output,
        CancellationToken ct)
    {
        var progress = invocation.Progress;
        var stage = "workflow-output";
        try
        {
            // Failed collection still reports established facts for the downstream gate.
            // It can never become successful diagnosis merely because output writing works.
            var outputCancellation = outcome.Status == "failed" ? CancellationToken.None : ct;
            outputCancellation.ThrowIfCancellationRequested();
            if (outcome.Status == "degraded")
            {
                stage = "limited-report";
                await WriteLimitedReportAsync(invocation, ct);
                stage = "workflow-output";
            }
            if (output is not null)
            {
                await GitHubActionsIntegration.WriteOutputsAsync(output, outcome, outputCancellation);
            }
            outputCancellation.ThrowIfCancellationRequested();
            if (outcome.Status == "failed")
            {
                await invocation.WriteDiagnosticsBestEffortAsync("failed");
                return 1;
            }
            ct.ThrowIfCancellationRequested();
            await invocation.WriteDiagnosticsAsync(outcome.Status);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ValidationException or OperationCanceledException)
        {
            var message = (stage == "limited-report" ? "Unable to write limited analysis report" : "Unable to write workflow outputs") +
                $" for run={outcome.RunUrl ?? outcome.RunId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"attempt={outcome.RunAttempt}: {ex.Message}";
            progress.Stage = stage;
            invocation.Capture.Failure ??= ex;
            progress.RecordProblem(AnalysisProblemCategory.Fatal, stage, message);
            Console.Error.WriteLine($"::error::{Display(DiagnosticTextSanitizer.Sanitize(message, limit: 1000))}");
            try
            {
                Directory.CreateDirectory(invocation.Evidence.RootDirectory);
                await invocation.WriteRecoveryBestEffortAsync("collection", "failed");
                await EvidencePackageStore.WriteOutcomeAsync(invocation.Evidence, progress.CreateOutcome("failed"));
            }
            catch (Exception finalization) when (finalization is IOException or UnauthorizedAccessException or JsonException or ValidationException)
            {
                progress.RecordProblem(AnalysisProblemCategory.Fatal, "finalization", finalization.Message);
                Console.Error.WriteLine($"::error::Unable to finalize workflow failure: {Display(DiagnosticTextSanitizer.Sanitize(finalization.Message, limit: 500))}");
            }
            await invocation.WriteDiagnosticsBestEffortAsync("failed");
            return 1;
        }
    }

    private static async Task WriteLimitedReportAsync(AnalysisInvocation invocation, CancellationToken ct)
    {
        var bundle = invocation.Capture.Bundle ?? throw new ValidationException("Limited analysis requires captured run evidence.");
        var runId = bundle.Run.Id.ToString(CultureInfo.InvariantCulture);
        var attempt = bundle.Run.Attempt.ToString(CultureInfo.InvariantCulture);
        var source = $"https://github.com/{bundle.Run.Repository}/actions/runs/{runId}/attempts/{attempt}";
        var artifacts = invocation.Progress.Problems.Count(problem => problem.Problem.Stage == "artifacts");
        var parsing = invocation.Progress.Problems.Count(problem => problem.Problem.Stage == "parsing");
        // Public summaries use only validated identity and fixed reason categories, never
        // agent prose or upstream diagnostic text. Full sanitized evidence stays in artifacts.
        var report = $"""
            # Limited CI analysis

            [Source CI run, attempt {attempt}]({source})

            Structured test results are unavailable. This is a limited-evidence outcome, not a complete diagnosis.

            - Captured failed jobs: {bundle.Jobs.Length}
            - Captured structured test failures: {bundle.TestEvidence.Failures.Length}
            - Confirmed runner-loss jobs: {bundle.Jobs.Count(job => job.RunnerFailure is not null)}
            - Artifact acquisition/selection problems: {artifacts}
            - Test-result parsing problems: {parsing}

            No causal history, issues, or PR analysis comments were published. Reruns are not authorized.

            Inspect the `ci-failure-evidence`, collection recovery, and collection diagnostics artifacts for source job annotations and acquisition details.
            """.ReplaceLineEndings("\n") + "\n";
        await File.WriteAllTextAsync(Path.Combine(invocation.Evidence.RootDirectory, "limited-analysis.md"), report, ct);
        await GitHubActionsIntegration.AppendStepSummaryAsync(report, ct);
        Console.WriteLine("::notice::Limited CI analysis recorded; unavailable test results prevent a complete diagnosis and reruns.");
    }
}
