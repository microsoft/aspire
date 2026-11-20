// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using AnalyzeCiFailure;

namespace Infrastructure.Tests;

/// <summary>Supplies controllable alternate-workflow artifact expectations for common collector contract tests.</summary>
internal sealed class AlternateWorkflowConventions(TestArtifactFormat format, TestEvidencePolicy policy) : IGitHubWorkflowConventions
{
    internal List<string> DescribedJobs { get; } = [];
    internal Dictionary<string, TestArtifactExpectation> AdditionalJobs { get; } = [];
    internal List<TestArtifactExpectation> AdditionalArtifacts { get; } = [];
    internal bool RejectDescriptions { get; init; }
    public string WorkflowPath => ".github/workflows/alternate.yml";

    public bool IsAggregationJob(JsonObject job)
        => CiFailureJson.Text(job["name"]) == "Alternate gate";

    public IReadOnlyList<TestArtifactExpectation> DescribeTestArtifacts(JsonObject job, int runAttempt)
    {
        var name = CiFailureJson.Text(job["name"])!;
        DescribedJobs.Add(name);
        if (RejectDescriptions)
        {
            throw new ValidationException("Alternate tests have an invalid artifact convention");
        }
        if (AdditionalJobs.TryGetValue(name, out var plan))
        {
            return [plan];
        }
        return name == "Alternate tests"
            ? [new TestArtifactExpectation(name, $"alternate-results-{runAttempt}", format, policy), .. AdditionalArtifacts]
            : [];
    }
}
