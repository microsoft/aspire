// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Checks that available aggregate report paths identify unique known test producers.</summary>
internal static partial class CombinedTrxReportGuard
{
    internal static async Task<int> ValidateAsync(
        string resultsDirectory, string repository, long runId, int runAttempt,
        GitHubApiClient githubApi, CancellationToken cancellationToken)
    {
        if (!RepositoryRegex().IsMatch(repository) || runId <= 0 || runAttempt <= 0)
        {
            throw new ValidationException("Expected --repo owner/name and positive --run-id and --run-attempt.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var reports = Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(resultsDirectory, file).Replace(Path.DirectorySeparatorChar, '/'))
            // Summary outputs are not reports from individual jobs. Do not ignore
            // arbitrary files under 'merged': only the established merged-*.trx shape.
            .Where(path => !MergedReportRegex().IsMatch(path))
            .Select((path, index) => (EntryPath: path, ExtractedFileName: $"{index:D5}.trx")).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (reports.Length == 0)
        {
            return 0;
        }

        var github = new AnalysisGitHubClient(githubApi, cancellationToken);
        var jobs = await github.GetRowsAsync(
            $"repos/{repository}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/attempts/{runAttempt.ToString(CultureInfo.InvariantCulture)}/jobs", "jobs");
        if (jobs.Count == 0 || jobs.Any(job => string.IsNullOrEmpty(Text(Field(job, "name")))))
        {
            throw new ValidationException("Combined report validation requires a nonempty named job inventory.");
        }
        var producers = await github.GetTestArtifactExpectationsForRunAsync(repository, runId, runAttempt, jobs, new AspireCiWorkflowConventions());
        var matching = new CombinedTrxReports(reports, producers);
        var assigned = producers.SelectMany(matching.GetFileNamesForJob).ToHashSet(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!assigned.Contains(report.ExtractedFileName))
            {
                throw new ValidationException($"Combined TRX report '{report.EntryPath}' has no unique known producing job. " +
                    "Check workflow job names, logs artifact names, aggregate runner directories, and AnalyzeCiFailure filename conventions.");
            }
        }

        return reports.Length;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(@"^(?:merged/merged-[^/]+|[^/]+/testresults/merged/merged-[^/]+)\.trx\z")]
    private static partial Regex MergedReportRegex();
}
