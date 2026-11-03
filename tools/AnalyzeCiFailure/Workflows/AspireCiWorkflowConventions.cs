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

    internal static bool HasCompleteCombinedResults(JsonArray jobs)
    {
        var finalJobs = jobs.OfType<JsonObject>().Where(job => Text(job["name"]) == "Tests / Final Test Results").ToArray();
        if (finalJobs.Length != 1)
        {
            return false;
        }
        // Upload runs under always(), even when an earlier download failed. Only
        // successful downloads of all three runner groups establish coverage.
        var steps = finalJobs[0]["steps"] as JsonArray ?? [];
        var downloads = steps.Where(step =>
            (Text(Field(step, "name")) ?? "").StartsWith("Run actions/download-artifact@", StringComparison.Ordinal)).ToArray();
        var uploads = steps.Where(step => Text(Field(step, "name")) == "Upload test results").ToArray();
        return downloads.Length == 3 && downloads.All(step => Text(Field(step, "conclusion")) == "success") &&
            uploads.Length == 1 && Text(Field(uploads[0], "conclusion")) == "success";
    }

    internal static int CombinedResultSpecificity(TestArtifactExpectation plan, string entry)
    {
        var match = TestArtifactJobNameRegex().Match(plan.JobName);
        if (plan.Format != TestArtifactFormat.Trx || !match.Success)
        {
            return 0;
        }
        // All-TestResults retains runner/testresults/<short>_<TFM>_<time>.trx,
        // plus <short>.trx (CLI tests) and <short>-<class>.trx (template batches).
        var prefix = match.Groups["runner"].Value + "/testresults/";
        if (!entry.StartsWith(prefix, StringComparison.Ordinal))
        {
            return 0;
        }
        var name = entry[prefix.Length..];
        var shortName = match.Groups["short"].Value;
        return !name.Contains('/') && (name == shortName + ".trx" ||
            name.StartsWith(shortName + "_", StringComparison.Ordinal) ||
            name.StartsWith(shortName + "-", StringComparison.Ordinal)) ? shortName.Length : 0;
    }

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
