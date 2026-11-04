// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure;

/// <summary>Indexes extracted TRX report filenames by their matching known job.</summary>
internal sealed class CombinedTrxReports
{
    private readonly Dictionary<TestArtifactExpectation, string[]> _fileNamesByJob;

    internal CombinedTrxReports(
        IEnumerable<(string EntryPath, string ExtractedFileName)> extractedReports,
        TestArtifactExpectation[] knownJobs)
    {
        // Include passing and historical jobs, retaining duplicate identities:
        // a runner and short name must identify exactly one known producer.
        var jobsByIdentity = knownJobs.Select(job => (Job: job, Identity: AspireCiWorkflowConventions.GetTestJobIdentity(job)))
            .Where(job => job.Identity is not null)
            .ToLookup(job => job.Identity!.Value, job => job.Job);

        _fileNamesByJob = extractedReports.Select(report => (
            report.ExtractedFileName,
            Job: FindMatchingJob(report.EntryPath, jobsByIdentity, knownJobs)))
            .Where(report => report.Job is not null)
            .GroupBy(report => report.Job!)
            .ToDictionary(group => group.Key, group => group.Select(report => report.ExtractedFileName).ToArray());
    }

    internal string[] GetFileNamesForJob(TestArtifactExpectation plan)
        => _fileNamesByJob.TryGetValue(plan, out var fileNames) ? fileNames : [];

    private static TestArtifactExpectation? FindMatchingJob(
        string entryPath,
        ILookup<(string Runner, string ShortName), TestArtifactExpectation> jobsByIdentity,
        TestArtifactExpectation[] knownJobs)
    {
        var identity = AspireCiWorkflowConventions.GetCombinedReportIdentity(entryPath);
        if (identity is null)
        {
            return null;
        }
        var exactMatches = jobsByIdentity[(identity.Value.Runner, identity.Value.ShortName)].ToArray();
        if (exactMatches.Length > 0 || identity.Value.IsStructured)
        {
            return exactMatches.Length == 1 ? exactMatches[0] : null;
        }

        return FindLegacyClassSuffixedJob(entryPath, knownJobs);
    }

    private static TestArtifactExpectation? FindLegacyClassSuffixedJob(string entryPath, TestArtifactExpectation[] knownJobs)
    {
        // Only legacy class-suffixed names need prefix ranking: Hosting-2-BatchTests
        // belongs to Hosting-2 rather than Hosting. Equally specific jobs stay ambiguous.
        var matches = knownJobs.Select(job => new
        {
            Plan = job,
            MatchingShortNameLength = AspireCiWorkflowConventions.GetLegacyClassSuffixedMatchLength(job, entryPath),
        }).Where(match => match.MatchingShortNameLength > 0).OrderByDescending(match => match.MatchingShortNameLength).ToArray();

        return matches.Length > 0 && (matches.Length == 1 || matches[0].MatchingShortNameLength > matches[1].MatchingShortNameLength)
            ? matches[0].Plan : null;
    }
}
