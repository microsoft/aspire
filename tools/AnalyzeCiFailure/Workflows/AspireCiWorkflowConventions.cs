// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Recognizes Aspire CI jobs and their required, optional and interrupted-execution result artifacts.</summary>
internal sealed partial class AspireCiWorkflowConventions : IGitHubWorkflowConventions
{
    internal const string ResultsUploadStep = "Upload logs, and test results";
    internal const string ExtensionTestStep = "Run extension E2E tests";
    internal const string FinalResultsJob = "Final Results";

    public string WorkflowPath => ".github/workflows/ci.yml";

    public bool IsAggregationJob(JsonObject job)
    {
        if (Text(job["name"]) == FinalResultsJob)
        {
            return true;
        }
        var firstFailure = (job["steps"] as JsonArray ?? [])
            .FirstOrDefault(step => Text(Field(step, "conclusion")) is "failure" or "cancelled" or "timed_out");

        return firstFailure is not null && AggregationStepNameRegex().IsMatch(Text(Field(firstFailure, "name")) ?? "");
    }

    public IReadOnlyList<TestArtifactExpectation> DescribeTestArtifacts(JsonObject job, int runAttempt)
    {
        var name = Text(job["name"]);
        if (name is null)
        {
            return [];
        }
        var steps = job["steps"] as JsonArray ?? [];
        var required = steps.Any(step => Text(Field(step, "name")) == ResultsUploadStep) ||
            RequiredTestJobNameRegex().IsMatch(name);
        var extension = ExtensionTestJobNameRegex().IsMatch(name);
        if (!required && !extension)
        {
            return [];
        }

        // Reusable workflow jobs appear as "... / Infrastructure (ubuntu-latest)".
        // Their logs artifacts are "logs-Infrastructure-ubuntu-latest"; extension
        // artifacts instead contain a runtime ID, shard and explicit attempt.
        var match = (required ? TestArtifactJobNameRegex() : ExtensionArtifactJobNameRegex()).Match(name);
        if (!match.Success)
        {
            throw new ValidationException("failed test job name does not match the artifact naming contract: " +
                Display(DiagnosticTextSanitizer.Sanitize(name, limit: 500)));
        }
        if (required)
        {
            return [new(
                name,
                $"logs-{match.Groups["short"].Value}-{match.Groups["runner"].Value}",
                TestArtifactFormat.Trx,
                TestEvidencePolicy.Required)];
        }

        var interrupted = !steps.Any(step => Text(Field(step, "conclusion")) == "failure") ||
            steps.Any(step => Text(Field(step, "status")) == "in_progress") ||
            steps.Any(step => Text(Field(step, "name")) == ExtensionTestStep &&
                Text(Field(step, "conclusion")) is "cancelled" or "timed_out");

        // Extension diagnostics are optional after setup failure or completed tests,
        // but runner loss/cancelled tests must not be interpreted as zero failures.
        return [new(
            name,
            $"extension-e2e-diagnostics-{(match.Groups["os"].Value == "Windows" ? "win-x64" : "linux-x64")}-{match.Groups["shard"].Value}-attempt{runAttempt}",
            TestArtifactFormat.Mocha,
            interrupted
                ? TestEvidencePolicy.RequiredForInterruptedExecution : TestEvidencePolicy.OptionalDiagnostics)];
    }

    [GeneratedRegex(@"^Tests / (No-package tests|Package tests - (Linux|Windows|macOS)|CLI archive tests)( \(| / )")]
    private static partial Regex RequiredTestJobNameRegex();

    [GeneratedRegex(@"^(Fail if|Check ).*(depend|failed)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AggregationStepNameRegex();

    [GeneratedRegex(@"(^| / )VS Code extension E2E( \(|$)")]
    private static partial Regex ExtensionTestJobNameRegex();

    [GeneratedRegex(@"(^| / )(?<short>[^/]+) \((?<runner>[^()]*)\)\z")]
    private static partial Regex TestArtifactJobNameRegex();

    [GeneratedRegex(@"(^| / )VS Code extension E2E \((?<os>Windows|Linux), (?<shard>[^()]+)\)\z")]
    private static partial Regex ExtensionArtifactJobNameRegex();
}
