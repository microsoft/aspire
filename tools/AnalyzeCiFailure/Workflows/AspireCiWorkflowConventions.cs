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

    /// <summary>Gets the runner bucket and test short name used to match a CI job to combined TRX reports.</summary>
    /// <remarks>
    /// For example, "Tests / Package tests - Linux / Hosting-2 (8-core-ubuntu-latest)"
    /// maps to ("ubuntu-latest", "Hosting-2"). Returns null for non-TRX jobs or unsupported job names or runners.
    /// </remarks>
    internal static (string RunnerBucket, string TestShortName)? GetTestJobIdentity(TestArtifactExpectation plan)
    {
        var match = TestArtifactJobNameRegex().Match(plan.JobName);
        if (plan.Format != TestArtifactFormat.Trx || !match.Success)
        {
            return null;
        }
        // tests.yml merges logs-*-ubuntu-latest into ubuntu-latest/, and likewise
        // for Windows/macOS. Only the aggregate identity uses these buckets;
        // source artifact names retain full labels such as 8-core-ubuntu-latest.
        var runner = match.Groups["runner"].Value;
        var bucket = runner switch
        {
            "ubuntu-latest" or "windows-latest" or "macos-latest" => runner,
            _ when runner.EndsWith("-ubuntu-latest", StringComparison.Ordinal) => "ubuntu-latest",
            _ when runner.EndsWith("-windows-latest", StringComparison.Ordinal) => "windows-latest",
            _ when runner.EndsWith("-macos-latest", StringComparison.Ordinal) => "macos-latest",
            _ => null,
        };
        return bucket is not null ? (bucket, match.Groups["short"].Value) : null;
    }

    /// <summary>Parses a TRX file's relative path inside All-TestResults to identify its producing runner bucket and test short name.</summary>
    /// <remarks>
    /// "ubuntu-latest/testresults/Hosting-2_net10.0_20261103065037.trx" maps to ("ubuntu-latest", "Hosting-2", true);
    /// "ubuntu-latest/testresults/Hosting-2.trx" maps to ("ubuntu-latest", "Hosting-2", false).
    /// Returns null for an unrecognized entry path. The suffix flag recognizes a filename pattern, not report contents.
    /// </remarks>
    internal static (string RunnerBucket, string TestShortName, bool HasFrameworkAndTimestampSuffix)? ParseCombinedTrxEntryPath(string archiveEntryPath)
    {
        var match = CombinedReportPathRegex().Match(archiveEntryPath);
        if (!match.Success)
        {
            return null;
        }
        var name = match.Groups["name"].Value;
        // Aspire.RepoTesting.targets emits <short>_<TargetFramework>_<yyyyMMddhhmmss>.trx;
        // run-tests.yml's direct-assembly steps emit <short>.trx. Parse the framework/timestamp
        // suffix from the right so '-' and '_' remain part of the full short name.
        var fileNameWithSuffix = TrxFileNameWithFrameworkAndTimestampRegex().Match(name);
        return (match.Groups["runner"].Value, fileNameWithSuffix.Success ? fileNameWithSuffix.Groups["short"].Value : name, fileNameWithSuffix.Success);
    }

    /// <summary>Returns the matching job short-name length for the class-suffix fallback, or zero when unmatched.</summary>
    /// <remarks>
    /// For "ubuntu-latest/testresults/Hosting-2-BatchTests.trx", jobs Hosting and Hosting-2
    /// in the same runner bucket score 7 and 9 respectively. The caller selects the unique longest match.
    /// </remarks>
    internal static int GetClassSuffixFallbackMatchLength(TestArtifactExpectation plan, string archiveEntryPath)
    {
        var identity = GetTestJobIdentity(plan);
        var match = CombinedReportPathRegex().Match(archiveEntryPath);
        if (identity is null || !match.Success || match.Groups["runner"].Value != identity.Value.RunnerBucket)
        {
            return 0;
        }
        var name = match.Groups["name"].Value;
        var prefix = identity.Value.TestShortName + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
        {
            return 0;
        }
        // An underscored suffix may belong to a report with framework/timestamp
        // fields from an unknown producer (or a malformed report name),
        // not a class suffix we can trust.
        return name.AsSpan(prefix.Length).Contains('_') ? 0 : identity.Value.TestShortName.Length;
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

    [GeneratedRegex(@"^(?<runner>[^/\\]+)/testresults/(?<name>[^/\\]+)\.trx\z")]
    private static partial Regex CombinedReportPathRegex();

    [GeneratedRegex(@"^(?<short>.+)_[^_]+_[0-9]{14}\z")]
    private static partial Regex TrxFileNameWithFrameworkAndTimestampRegex();

    [GeneratedRegex(@"(^| / )VS Code extension E2E \((?<os>Windows|Linux), (?<shard>[^()]+)\)\z")]
    private static partial Regex ExtensionArtifactJobNameRegex();
}
