// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using Aspire.TestTools;

namespace AnalyzeCiFailure;

/// <summary>Associates an unnormalized structured test failure with its producing job and result source.</summary>
internal sealed record RawTestFailure(long JobId, string JobName, string Name, CollectedDiagnostics Diagnostics, TestResultSource Source);

/// <summary>Reads supported test reports and consolidates copied executions while preserving distinct raw observations.</summary>
internal static class TestFailureReader
{
    internal static ImmutableArray<RawTestFailure> Read(
        string resultsDirectory,
        ImmutableArray<JobEvidence> jobs,
        IReadOnlyList<SelectedTestArtifact> artifacts)
        => Read(resultsDirectory, jobs, artifacts, reportProblem: null, reportNonTestFailure: null);

    internal static ImmutableArray<RawTestFailure> ReadAvailable(
        string resultsDirectory,
        ImmutableArray<JobEvidence> jobs,
        IReadOnlyList<SelectedTestArtifact> artifacts,
        Action<string> reportProblem,
        Action<SelectedTestArtifact, string> reportNonTestFailure)
    {
        var result = ImmutableArray.CreateBuilder<RawTestFailure>();
        foreach (var artifact in artifacts)
        {
            try
            {
                result.AddRange(Read(resultsDirectory, jobs, [artifact], reportProblem, reportNonTestFailure));
            }
            catch (Exception error) when (error is ValidationException or IOException)
            {
                reportProblem(error.Message);
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<RawTestFailure> Read(
        string resultsDirectory,
        ImmutableArray<JobEvidence> jobs,
        IReadOnlyList<SelectedTestArtifact> artifacts,
        Action<string>? reportProblem,
        Action<SelectedTestArtifact, string>? reportNonTestFailure)
    {
        var result = ImmutableArray.CreateBuilder<RawTestFailure>();
        foreach (var artifact in artifacts)
        {
            var artifactId = GitHubJson.ReadRequiredId(artifact.Artifact.Id);
            var producingJobs = jobs.Where(job => job.Name == artifact.JobName).ToArray();
            if (producingJobs.Length != 1)
            {
                throw new ValidationException($"Cannot associate test artifact {artifactId} with a failed job: expected one job named " +
                    $"'{CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(artifact.JobName, limit: 500))}', but found {producingJobs.Length}. " +
                    $"artifact='{CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(artifact.Artifact.Name ?? "", limit: 100))}'");
            }

            var job = producingJobs[0];
            var directory = Path.Combine(resultsDirectory, artifactId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory) && !artifact.RequiresResults)
            {
                continue;
            }

            var relativeDirectory = "extracted-results/" + artifactId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!Directory.Exists(directory))
            {
                throw ArtifactError($"Cannot read test results for artifact {artifactId}: extracted directory '{relativeDirectory}' does not exist.", artifact);
            }

            var files = Directory.GetFiles(directory, artifact.Format == TestArtifactFormat.Trx ? "*.trx" : "*.json")
                .Order(StringComparer.Ordinal).ToArray();
            if (artifact.RequiresResults && files.Length == 0)
            {
                throw ArtifactError(
                    artifact.Format == TestArtifactFormat.Trx
                    ? $"Cannot read TRX results for artifact {artifactId}: no '*.trx' files were found in '{relativeDirectory}'."
                    : $"Cannot read Mocha results for artifact {artifactId}: no result file was found in '{relativeDirectory}'.",
                    artifact);
            }
            if (artifact.Format == TestArtifactFormat.Mocha && files.Length > 1)
            {
                throw ArtifactError($"Cannot read Mocha results for artifact {artifactId}: expected at most one JSON result in '{relativeDirectory}', but found {files.Length}.", artifact);
            }
            foreach (var file in files)
            {
                try
                {
                    if (artifact.Format == TestArtifactFormat.Trx)
                    {
                        // TRX executionId identifies copied executions; testName retains the exact
                        // data-row identity, e.g. testName="Tests.Method(value: a/b)".
                        foreach (var test in (TrxReader.DeserializeTrxFile(file)?.Results?.UnitTestResults
                                              ?? []).Where(test => test.Outcome == "Failed"))
                        {
                            Add(
                                name: test.TestName ?? "",
                                error: test.Output?.ErrorMessage ?? "",
                                stack: test.Output?.StackTrace ?? "",
                                stdout: test.Output?.StdOut ?? "",
                                stderr: test.Output?.StdErr ?? "",
                                execution: test.ExecutionId);
                        }
                    }
                    else
                    {
                        var report = MochaReader.Read(file);
                        var completed = report.CompletedTests.ToHashSet(StringComparer.Ordinal);
                        foreach (var test in report.Failures)
                        {
                            // Mocha reports both a completed assertion and an "after each"
                            // hook in failures[], but only the assertion appears in tests[].
                            // Exclude the hook/harness entry, not unrelated completed failures.
                            if (!completed.Contains(test.Name) || BlockingHarnessError(test))
                            {
                                reportNonTestFailure?.Invoke(
                                    artifact,
                                    $"job={job.Id} artifact={artifactId} result={relativeDirectory}/{Path.GetFileName(file)}\n" +
                                    $"{test.Name}\n{test.ErrorName}: {test.ErrorMessage}\n{test.StackTrace}");
                                continue;
                            }
                            Add(
                                name: test.Name,
                                error: test.ErrorMessage,
                                stack: test.StackTrace,
                                stdout: "",
                                stderr: "",
                                execution: null);
                        }
                    }
                    void Add(string name, string error, string stack, string stdout, string stderr, string? execution)
                    {
                        if (string.IsNullOrEmpty(name) || DiagnosticTextSanitizer.Sanitize(name).Length == 0)
                        {
                            throw new ValidationException("Collected test failures must have a nonempty test name.");
                        }
                        result.Add(new(
                            job.Id,
                            job.Name,
                            name,
                            new CollectedDiagnostics { Error = error, StackTrace = stack, StandardOutput = stdout, StandardError = stderr },
                            new TestResultSource
                            {
                                ArtifactId = artifactId,
                                ResultFile = Path.GetFileName(file),
                                Format = artifact.Format,
                                ExecutionId = execution is { Length: > 0 } ? execution : null,
                            }));
                    }
                }
                catch (Exception ex) when (ex is XmlException or JsonException or ValidationException or IOException or InvalidOperationException)
                {
                    var error = ArtifactError(
                        $"Cannot parse test result '{CiFailureJson.Display(relativeDirectory + "/" + Path.GetFileName(file))}' from artifact {artifactId}: " +
                        CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(ex.GetBaseException().Message, limit: 160)),
                        artifact);
                    if (reportProblem is null)
                    {
                        throw error;
                    }
                    reportProblem(error.Message);
                }
            }
        }
        return result.ToImmutable();
    }

    private static ValidationException ArtifactError(string message, SelectedTestArtifact artifact)
        => new($"{message}; {TestResultArtifacts.Describe(artifact.Expectation)}");

    internal static ImmutableArray<CollectedTestFailure> ConsolidateFailures(ImmutableArray<RawTestFailure> inputs)
    {
        var failures = new List<(RawTestFailure Raw, List<TestResultSource> Sources)>();
        var executions = new Dictionary<(long Job, string Execution), int>();
        foreach (var input in inputs)
        {
            var source = input.Source.ExecutionId is { Length: 0 } ? input.Source with { ExecutionId = null } : input.Source;
            if (string.IsNullOrEmpty(input.Name) || DiagnosticTextSanitizer.Sanitize(input.Name).Length == 0)
            {
                throw new ValidationException("Collected test failures must have a nonempty test name. " +
                    $"job={input.JobId} artifact={source.ArtifactId} file={CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(source.ResultFile, limit: 80))}");
            }
            if (source.ExecutionId is { } execution)
            {
                if (executions.TryGetValue((input.JobId, execution), out var index))
                {
                    var previous = failures[index];
                    // Compare raw, unbounded diagnostics before redaction or truncation can hide conflicts.
                    if (previous.Raw.Name != input.Name || previous.Raw.Diagnostics != input.Diagnostics)
                    {
                        throw new ValidationException("Conflicting diagnostics for the same test execution: " +
                            $"job={input.JobId} execution='{CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(execution, limit: 80))}' " +
                            $"artifacts={previous.Raw.Source.ArtifactId},{source.ArtifactId} " +
                            $"test='{CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(input.Name, limit: 80))}'");
                    }
                    if (!previous.Sources.Contains(source))
                    {
                        previous.Sources.Add(source);
                    }
                    continue;
                }
                executions.Add((input.JobId, execution), failures.Count);
            }
            failures.Add((input, [source]));
        }
        return failures.OrderBy(item => item.Raw.Name, StringComparer.Ordinal)
            .ThenBy(item => DiagnosticTextSanitizer.Sanitize(item.Raw.JobName, limit: 500), StringComparer.Ordinal)
            .Select((item, index) => new CollectedTestFailure
            {
                Ref = $"test-{index + 1}",
                JobId = item.Raw.JobId,
                Name = item.Raw.Name,
                DisplayName = DiagnosticTextSanitizer.Sanitize(item.Raw.Name, limit: 500),
                Diagnostics = new CollectedDiagnostics
                {
                    Error = DiagnosticTextSanitizer.Sanitize(item.Raw.Diagnostics.Error, limit: 1000, multiline: true),
                    StackTrace = DiagnosticTextSanitizer.Sanitize(item.Raw.Diagnostics.StackTrace, limit: 2000, multiline: true),
                    StandardOutput = DiagnosticTextSanitizer.Sanitize(item.Raw.Diagnostics.StandardOutput, limit: 4000, multiline: true),
                    StandardError = DiagnosticTextSanitizer.Sanitize(item.Raw.Diagnostics.StandardError, limit: 4000, multiline: true),
                },
                Sources = item.Sources.ToImmutableArray(),
            }).ToImmutableArray();
    }

    private static bool BlockingHarnessError(MochaFailure failure)
        => failure.ErrorName is "InvalidSessionIdError" or "NoSuchSessionError" or "NoSuchWindowError" or "SessionNotCreatedError" ||
            failure.ErrorName == "WebDriverError" && new[]
            {
                "session deleted because of page crash", "disconnected: not connected to devtools", "chrome not reachable",
            }.Any(pattern => failure.ErrorMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}
