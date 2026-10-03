// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnalyzeCiFailure.Common;
using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Common.WorkflowCommands;
using static AnalyzeCiFailure.Validation.ValidationException;

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// Reads and sanitizes every file validation looks at, and turns a file that cannot be read into
/// the validation error for the rule it breaks. Nothing here decides whether the analysis is right;
/// that is <see cref="ValidationRules"/>.
/// </summary>
internal sealed class ValidationInputs(ValidationPaths paths, PublicationScripts scripts, string scratchDirectory)
{
    /// <summary>
    /// The sanitized copy of the trusted failed jobs. It is kept outside the analysis directory so
    /// it is never uploaded with the agent's files.
    /// </summary>
    public string TrustedFailedJobsFile { get; } = Path.Combine(scratchDirectory, "trusted-failed-jobs.json");

    public void RequireAllPresent()
        => Require(
            new[] { paths.AnalysisFile, paths.RunContextFile, paths.FailedJobsFile, paths.TestEvidenceFile, paths.RunFile }.All(File.Exists),
            "Analysis result or trusted run data not found");

    /// <summary>Sanitizes the agent's analysis in place, so every later step reads the sanitized copy.</summary>
    public Task SanitizeAnalysisAsync(CancellationToken cancellationToken)
        => scripts.SanitizeInPlaceAsync("sanitize-analysis", paths.AnalysisFile, cancellationToken);

    public TrustedRun ReadTrustedRun()
    {
        var runContext = ReadTrusted<RunContextDocument>(paths.RunContextFile);
        var runMetadata = ReadTrusted<RunMetadataDocument>(paths.RunFile, "Trusted run metadata is invalid");

        // run.json comes from the GitHub API. Its html_url is the link published in the PR comment,
        // so it must point at this exact run on github.com.
        var runUrl = runMetadata.HtmlUrl ?? "";
        Require(
            runMetadata.Id == runContext.RunId &&
            Regex.IsMatch(runUrl, $@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/actions/runs/{runContext.RunId}\z"),
            "Trusted run metadata is invalid");

        // The subject PR decides where the comment is posted. An empty value means the run has no
        // single associated PR; anything else must be exactly one PR number.
        long? prNumber = null;
        if (runContext.RunScope == RunScopes.PullRequest && !string.IsNullOrEmpty(runContext.PrNumbers))
        {
            Require(
                long.TryParse(runContext.PrNumbers, NumberStyles.None, CultureInfo.InvariantCulture, out var number),
                "Trusted run context must contain one unambiguous subject PR");
            prNumber = number;
        }

        return new TrustedRun(runContext.RunId, runContext.RunScope, runUrl, prNumber);
    }

    /// <summary>Binds analysis-result.json, or part of it, to <typeparamref name="T"/>.</summary>
    public T ReadAnalysis<T>(TrustedRun run)
    {
        try
        {
            return JsonFiles.Read<T>(paths.AnalysisFile);
        }
        catch (JsonException ex)
        {
            // A value of the wrong JSON type cannot bind, so report the rule that the field's
            // value would have broken. Example paths: "$.pr", "$.failed_jobs[0].id",
            // "$.failed_tests[1].stack_trace", "$.causes[2]".
            var path = ex.Path ?? "$";
            throw new ValidationException(path switch
            {
                "$.run_id" or "$.run_scope" => ValidationMessages.RunContextMismatch,
                _ when IsWithin(path, "$.pr") => run.Scope == RunScopes.Main
                    ? ValidationMessages.MainRunHasPullRequest
                    : ValidationMessages.UntrustedPullRequest,
                _ when IsWithin(path, "$.failed_tests") || IsFailedJobField(path, "reason") => ValidationMessages.UnsafeFailedTests,
                _ when IsFailedJobField(path, "classification") => ValidationMessages.UnclassifiedJobs,
                _ when IsWithin(path, "$.failed_jobs") || IsWithin(path, "$.causes") => ValidationMessages.InvalidIdArrays,
                _ => $"Analysis field {Display(path)} has an invalid value",
            });
        }

        static bool IsWithin(string path, string property)
            => path == property || path.StartsWith(property + "[", StringComparison.Ordinal) || path.StartsWith(property + ".", StringComparison.Ordinal);

        static bool IsFailedJobField(string path, string field)
            => IsWithin(path, "$.failed_jobs") && path.EndsWith("." + field, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the jobs that really failed. Job names are collected from the GitHub API but can still
    /// contain PR-controlled text (matrix values), so they go through the same sanitizer as agent output.
    /// </summary>
    public async Task<List<TrustedJob>> ReadTrustedFailedJobsAsync(CancellationToken cancellationToken)
    {
        const string InvalidJobs = "Trusted failed jobs are invalid";
        Require(
            await scripts.TrySanitizeAsync("sanitize-trusted-failed-jobs", paths.FailedJobsFile, TrustedFailedJobsFile, cancellationToken),
            InvalidJobs);
        return ReadTrusted<List<TrustedJob>>(TrustedFailedJobsFile, InvalidJobs);
    }

    /// <summary>Reads test-evidence.json and, when results were collected, the sanitized test failures.</summary>
    public async Task<TestEvidence> ReadTestEvidenceAsync(CancellationToken cancellationToken)
    {
        const string InvalidState = "Trusted test evidence state is invalid";
        var state = ReadTrusted<TestEvidenceDocument>(paths.TestEvidenceFile, InvalidState).State;
        switch (state)
        {
            case TestEvidenceStates.Unavailable or TestEvidenceStates.NotApplicable:
                return new TestEvidence(state, Failures: null);
            case TestEvidenceStates.Complete:
                break;
            default:
                throw new ValidationException(InvalidState);
        }

        var trustedTestsFile = Path.Combine(scratchDirectory, "trusted-test-failures.json");
        Require(
            File.Exists(paths.TestFailuresFile) &&
            await scripts.TrySanitizeAsync("sanitize-trusted-test-failures", paths.TestFailuresFile, trustedTestsFile, cancellationToken),
            ValidationMessages.TestEvidenceMismatch);

        return new TestEvidence(state, ReadTrusted<List<TrustedTestFailure>>(trustedTestsFile, ValidationMessages.TestEvidenceMismatch));
    }

    /// <summary>
    /// Replaces the agent's copy of each test's diagnostics with the trusted (and redacted) test
    /// results, so the published error text cannot be paraphrased or fabricated. The file is edited
    /// rather than re-serialized so fields the validator does not model are preserved.
    /// </summary>
    public void ReplaceTestDiagnostics(List<TrustedTestFailure> trustedFailures)
    {
        var trustedByPair = trustedFailures.ToDictionary(test => (test.Test, test.Job));
        JsonFiles.Update(paths.AnalysisFile, root =>
        {
            foreach (var reported in root["failed_tests"]!.AsArray().Select(test => test!.AsObject()))
            {
                var trusted = trustedByPair[((string)reported["name"]!, (string)reported["job"]!)];
                reported["error"] = trusted.Error;
                reported["stack_trace"] = trusted.StackTrace;
                reported["standard_output"] = trusted.StandardOutput;
                reported["standard_error"] = trusted.StandardError;
            }
        });
    }

    public string[] ListCauseFiles()
    {
        if (!Directory.Exists(paths.CausesDirectory))
        {
            return [];
        }

        // Dotfiles are skipped; they are not agent-written causes. Ordinal order keeps the first
        // reported error stable across platforms.
        return Directory.GetFiles(paths.CausesDirectory, "*.json", new EnumerationOptions { MatchCasing = MatchCasing.CaseSensitive })
            .Where(file => !Path.GetFileName(file).StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Sanitizes a cause file in place and binds it, rejecting any field the cause contract does not declare.</summary>
    public async Task<CauseDocument> ReadCauseAsync(string causeFile, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(causeFile);
        Require(JsonFiles.IsValidJson(causeFile), $"Invalid JSON in cause file: {Display(fileName)}");
        await scripts.SanitizeInPlaceAsync("sanitize-cause", causeFile, cancellationToken);

        try
        {
            return JsonFiles.Read<CauseDocument>(causeFile, JsonFiles.StrictOptions);
        }
        catch (JsonException)
        {
            throw new ValidationException(ValidationMessages.UnsupportedCauseFields(fileName));
        }
    }

    /// <summary>The cause with the same file name stored by an earlier run, or null when there is none.</summary>
    public PriorCauseDocument? ReadPriorCause(string causeFileName)
    {
        var priorCauseFile = paths.PriorCauseFile(causeFileName);
        return File.Exists(priorCauseFile) ? ReadTrusted<PriorCauseDocument>(priorCauseFile) : null;
    }

    /// <summary>Reads a trusted document. <paramref name="invalidMessage"/> is reported when it does not have the expected shape.</summary>
    private static T ReadTrusted<T>(string path, string? invalidMessage = null)
    {
        try
        {
            return JsonFiles.Read<T>(path);
        }
        catch (JsonException)
        {
            throw new ValidationException(invalidMessage ?? $"Trusted input {Display(Path.GetFileName(path))} is not valid JSON");
        }
    }
}
