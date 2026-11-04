// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Writes GitHub Actions outputs and summaries and interprets completion job results.</summary>
internal static class GitHubActionsIntegration
{

    internal static async Task WriteOutputsAsync(string path, AnalysisOutcome outcome, CancellationToken ct)
    {
        var outputs = new (string Name, string? Value)[]
        {
            ("run_id", outcome.RunId?.ToString(CultureInfo.InvariantCulture)),
            ("run_attempt", outcome.RunAttempt?.ToString(CultureInfo.InvariantCulture)),
            ("head_sha", outcome.HeadSha),
            ("run_url", outcome.RunUrl),
            ("run_scope", outcome.RunScope is { } scope ? EnumText(scope) : null),
            ("pr_numbers", outcome.RunScope is null ? null : outcome.SubjectPrNumber?.ToString(CultureInfo.InvariantCulture) ?? ""),
            ("has_work", outcome.HasRelevantFailures is { } hasWork ? hasWork ? "true" : "false" : null),
            ("analysis_ready", outcome.Status == "ready" ? "true" : "false"),
            ("collection_status", outcome.Status),
        };
        var batch = new StringBuilder();
        foreach (var (name, value) in outputs)
        {
            if (value is null)
            {
                continue;
            }
            if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
            {
                throw new ValidationException($"Invalid workflow output value for {name}");
            }
            batch.Append(name).Append('=').Append(value).Append('\n');
        }
        ct.ThrowIfCancellationRequested();
        if (batch.Length > 0)
        {
            // GitHub file commands use UTF-8 "name=value" lines. Validate the entire
            // batch before opening the runner-owned file; do not emit a partial batch
            // when a later value contains a newline.
            // https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands#setting-an-output-parameter
            await File.AppendAllTextAsync(path, batch.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    internal static void EnsureExpectedCompletion(string? hasRelevantFailures, string? publicationResult, string? collectionStatus, string? collectionResult)
    {
        if (hasRelevantFailures is not (null or "" or "true" or "false"))
        {
            throw new ValidationException("HAS_WORK must be true, false, or empty.");
        }
        if (hasRelevantFailures == "true" && collectionStatus == "degraded" && collectionResult == "success")
        {
            Console.WriteLine("::notice::Collection completed with a limited-evidence report; complete diagnosis and publication were not authorized.");
            return;
        }
        if (hasRelevantFailures == "true" && publicationResult != "success")
        {
            throw new ValidationException("CI failure analysis did not reach an accepted terminal outcome " +
                $"(collection status: {Display(DiagnosticTextSanitizer.Sanitize(collectionStatus ?? "", limit: 100))}; " +
                $"collection result: {Display(DiagnosticTextSanitizer.Sanitize(collectionResult ?? "", limit: 100))}; " +
                $"publication result: {Display(DiagnosticTextSanitizer.Sanitize(publicationResult ?? "", limit: 100))}). Check the collection and publication logs.");
        }

    }

    internal static async Task AppendStepSummaryAsync(string markdown, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            await File.AppendAllTextAsync(summary, markdown, ct);
        }
    }
}
