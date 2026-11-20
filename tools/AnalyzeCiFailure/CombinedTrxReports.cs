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
        string archiveEntryPath,
        ILookup<(string RunnerBucket, string TestShortName), TestArtifactExpectation> jobsByIdentity,
        TestArtifactExpectation[] knownJobs)
    {
        var identity = AspireCiWorkflowConventions.ParseCombinedTrxEntryPath(archiveEntryPath);
        if (identity is null)
        {
            return null;
        }
        var exactMatches = jobsByIdentity[(identity.Value.RunnerBucket, identity.Value.TestShortName)].ToArray();
        // A recognized framework/timestamp suffix identifies the full producer name.
        // Do not attribute an unknown producer's report to a shorter job-name prefix:
        // Hosting-Unknown_net10.0_20261103065037.trx must not match Hosting.
        if (exactMatches.Length > 0 || identity.Value.HasFrameworkAndTimestampSuffix)
        {
            return exactMatches.Length == 1 ? exactMatches[0] : null;
        }

        return FindJobUsingClassSuffixFallback(archiveEntryPath, knownJobs);
    }

    private static TestArtifactExpectation? FindJobUsingClassSuffixFallback(string archiveEntryPath, TestArtifactExpectation[] knownJobs)
    {
        // Only class-suffixed names need prefix ranking: Hosting-2-BatchTests
        // belongs to Hosting-2 rather than Hosting. Equally specific jobs stay ambiguous.
        var matches = knownJobs.Select(job => new
        {
            Plan = job,
            MatchingShortNameLength = AspireCiWorkflowConventions.GetClassSuffixFallbackMatchLength(job, archiveEntryPath),
        }).Where(match => match.MatchingShortNameLength > 0).OrderByDescending(match => match.MatchingShortNameLength).ToArray();

        return matches.Length > 0 && (matches.Length == 1 || matches[0].MatchingShortNameLength > matches[1].MatchingShortNameLength)
            ? matches[0].Plan : null;
    }
}
