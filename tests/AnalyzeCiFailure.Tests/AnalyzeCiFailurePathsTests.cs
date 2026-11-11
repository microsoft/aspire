// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using AnalyzeCiFailure;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks evidence layout and history-path containment, including symbolic-link aliases.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    public async Task EvidenceLayoutContainsNamedPathsWithoutCreatingFiles()
    {
        var workingDirectory = Path.Combine(_workspace.Path, "working directory");
        var root = Path.Combine(workingDirectory, "ci-failure-analysis/evidence");
        var paths = new EvidencePaths(root);
        string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

        Assert.Equal(root, paths.RootDirectory);
        Assert.False(Directory.Exists(workingDirectory));
        await Verifier.Verify(
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["Run"] = Relative(paths.Run),
                    ["Evidence"] = Relative(paths.Bundle),
                    ["CollectionOutcome"] = Relative(paths.Outcome),
                    ["AllJobs"] = Relative(paths.AllJobs),
                    ["FailedJobs"] = Relative(paths.FailedJobs),
                    ["RetryPatterns"] = Relative(paths.RetryPatterns),
                    ["TriggeringMerge"] = Relative(paths.TriggeringMerge),
                    ["LastSuccessfulMainRun"] = Relative(paths.LastSuccessfulMainRun),
                    ["CandidateMerges"] = Relative(paths.CandidateMerges),
                    ["CandidateHistoryStatus"] = Relative(paths.CandidateHistoryStatus),
                    ["PullRequestFiles"] = Relative(paths.PullRequestFiles),
                    ["PullRequestMetadata"] = Relative(paths.PullRequestMetadata),
                    ["Artifacts"] = Relative(paths.Artifacts),
                    ["SelectedTestArtifacts"] = Relative(paths.SelectedTestArtifacts),
                    ["PriorCauseIndex"] = Relative(paths.PriorCauseIndex),
                    ["Summary"] = Relative(paths.Summary),
                    ["PriorCausesDirectory"] = Relative(paths.PriorCausesDirectory),
                    ["JobLog"] = Relative(paths.JobLog("123.0")),
                    ["JobAnnotations"] = Relative(paths.JobAnnotations("123.0")),
                    ["PriorCause"] = Relative(paths.PriorCause("network-timeout.json")),
                },
                new JsonSerializerOptions { WriteIndented = true }),
            "json").UseDirectory("Snapshots");
    }
}
