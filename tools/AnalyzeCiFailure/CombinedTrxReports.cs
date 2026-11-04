// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

/// <summary>Indexes extracted TRX report filenames by their best-matching known job.</summary>
internal sealed class CombinedTrxReports
{
    private readonly Dictionary<TestArtifactExpectation, string[]> _fileNamesByJob;

    internal CombinedTrxReports(
        IEnumerable<(string EntryPath, string ExtractedFileName)> extractedReports,
        TestArtifactExpectation[] knownJobs)
    {
        _fileNamesByJob = extractedReports.Select(report => (
            report.ExtractedFileName,
            Job: FindBestMatchingJob(report.EntryPath, knownJobs)))
            .Where(report => report.Job is not null)
            .GroupBy(report => report.Job!)
            .ToDictionary(group => group.Key, group => group.Select(report => report.ExtractedFileName).ToArray());
    }

    internal string[] GetFileNamesForJob(TestArtifactExpectation plan)
        => _fileNamesByJob.TryGetValue(plan, out var fileNames) ? fileNames : [];

    private static TestArtifactExpectation? FindBestMatchingJob(string entryPath, TestArtifactExpectation[] knownJobs)
    {
        // Include passing and historical jobs so their reports cannot match an
        // actionable job merely because the short names overlap.
        // Hosting-2_*.trx belongs to Hosting-2, not Hosting. Prefer the
        // longest matching short name; equally specific jobs stay ambiguous.
        var matches = knownJobs.Select(job => new
        {
            Plan = job,
            MatchingShortNameLength = AspireCiWorkflowConventions.GetMatchingTestShortNameLength(job, entryPath),
        }).Where(match => match.MatchingShortNameLength > 0).OrderByDescending(match => match.MatchingShortNameLength).ToArray();

        return matches.Length > 0 && (matches.Length == 1 || matches[0].MatchingShortNameLength > matches[1].MatchingShortNameLength)
            ? matches[0].Plan : null;
    }
}
