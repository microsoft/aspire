// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.TestTools;

namespace CreateFailingTestIssue;

internal static class FailingTestIssueCommand
{
    private static readonly HashSet<string> s_failedOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed",
        "Error",
        "Timeout",
        "Aborted"
    };

    private static readonly HashSet<string> s_failedConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "failure",
        "timed_out",
        "cancelled",
        "startup_failure"
    };

    private static readonly Regex s_failedTestPattern = new(@"(?:^|\r?\n)\s*failed\s+(?<name>.+?)\s*(?:\[|\()", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_logPrefixPattern = new(@"^\d{4}-\d{2}-\d{2}T[^\s]+\s+", RegexOptions.Compiled);
    private static readonly Regex s_logSummaryPattern = new(@"<details><summary>.*?<b>(?<name>.+?)</b></summary>", RegexOptions.Compiled);
    private static readonly Regex s_directFailedLinePattern = new(@"^\s*failed\s+(?<name>.+?)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_inlineStackPattern = new(@"^(?<error>.*?)(?:\s{2,}|\t+)(?<stack>at .+)$", RegexOptions.Compiled);

    public static async Task<CreateFailingTestIssueResult> ExecuteAsync(CommandInput input, CancellationToken cancellationToken)
    {
        input = input with { TestQuery = string.IsNullOrWhiteSpace(input.TestQuery) ? null : input.TestQuery.Trim() };
        List<string> executionLog = [];
        List<string> warnings = [];
        WorkflowSection? workflowSection = null;
        ResolutionSection? resolutionSection = null;
        void Log(string message) => executionLog.Add(message);

        try
        {
            var workflowSelectorResolution = FailingTestIssueLogic.ResolveWorkflowSelector(input.WorkflowSelector);
            if (!workflowSelectorResolution.Success || workflowSelectorResolution.ResolvedWorkflowFile is null)
            {
                var errorMessage = workflowSelectorResolution.ErrorMessage ?? "Unable to resolve the workflow selector.";
                Log(errorMessage);
                return CreateFailureResult(input, workflowSection, resolutionSection, errorMessage, executionLog, warnings, []);
            }

            Log($"Resolved workflow selector '{workflowSelectorResolution.Requested}' to '{workflowSelectorResolution.ResolvedWorkflowFile}'.");
            var workflowMetadata = await ResolveWorkflowAsync(input.Repository, workflowSelectorResolution.ResolvedWorkflowFile, cancellationToken).ConfigureAwait(false);
            Log($"Resolved workflow metadata: '{workflowMetadata.Name}' ({workflowMetadata.Path}).");
            workflowSection = new WorkflowSection(
                Requested: workflowSelectorResolution.Requested,
                ResolvedWorkflowFile: workflowMetadata.Path,
                ResolvedWorkflowName: workflowMetadata.Name);

            var sourceResolution = FailingTestIssueLogic.ParseSourceUrl(input.SourceUrl, input.Repository);
            if (!sourceResolution.Success || sourceResolution.SourceKind is null)
            {
                var errorMessage = sourceResolution.ErrorMessage ?? "Unable to resolve the source URL.";
                Log(errorMessage);
                return CreateFailureResult(input, workflowSection, resolutionSection, errorMessage, executionLog, warnings, []);
            }

            Log($"Parsed source URL as '{sourceResolution.SourceKind}'.");
            var resolvedRun = await ResolveRunAsync(input.Repository, workflowMetadata, sourceResolution, cancellationToken).ConfigureAwait(false);
            Log($"Resolved workflow run {resolvedRun.RunId} attempt {resolvedRun.RunAttempt} with status '{resolvedRun.Status}'.");
            resolutionSection = new ResolutionSection(
                SourceKind: sourceResolution.SourceKind,
                RunId: resolvedRun.RunId,
                RunUrl: resolvedRun.RunUrl,
                RunAttempt: resolvedRun.RunAttempt,
                PullRequestNumber: resolvedRun.PullRequestNumber,
                JobUrls: []);

            var isCompletedRun = string.Equals(resolvedRun.Status, "completed", StringComparison.OrdinalIgnoreCase);
            if (!isCompletedRun)
            {
                warnings.Add($"The selected workflow run is not completed yet (status: {resolvedRun.Status}). Results may be incomplete.");
                Log("Run is still active; results may be incomplete.");
            }

            var failedJobs = await ListFailedJobsAsync(input.Repository, resolvedRun, cancellationToken).ConfigureAwait(false);
            Log($"Found {failedJobs.Count} failed job(s).");
            resolutionSection = resolutionSection with { JobUrls = failedJobs.Select(job => job.HtmlUrl).Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().ToArray() };

            var jobLogs = failedJobs.Count == 0
                ? []
                : await DownloadFailedTestsFromLogsAsync(input.Repository, failedJobs, warnings, Log, cancellationToken).ConfigureAwait(false);
            var failedTestsByJobUrl = jobLogs
                .Where(static jobLog => !string.IsNullOrWhiteSpace(jobLog.Job.HtmlUrl))
                .ToDictionary(static jobLog => jobLog.Job.HtmlUrl, static jobLog => jobLog.FailedTests, StringComparer.OrdinalIgnoreCase);
            var trxOccurrences = await DownloadFailedTestOccurrencesAsync(input.Repository, resolvedRun.RunId, allowArtifactFallback: isCompletedRun, Log, cancellationToken).ConfigureAwait(false);
            var logOccurrences = jobLogs.SelectMany(static jobLog => jobLog.Occurrences).ToList();
            Log($"Collected {trxOccurrences.Count} artifact-derived occurrence(s) and {logOccurrences.Count} log-derived occurrence(s).");
            var occurrences = trxOccurrences.Count > 0 ? trxOccurrences : logOccurrences;
            var availableFailedTests = GetAvailableFailedTests(occurrences);

            if (occurrences.Count == 0)
            {
                var message = isCompletedRun
                    ? "The workflow run did not contain any failed test results in downloadable .trx artifacts."
                    : "The workflow run has not produced any failed test results in downloadable .trx artifacts yet.";
                Log(message);
                return CreateFailureResult(input, workflowSection, resolutionSection, message, executionLog, warnings, availableFailedTests);
            }

            if (trxOccurrences.Count == 0)
            {
                warnings.Add("No downloadable .trx artifacts were available. Falling back to failed job logs.");
                Log("Using log-derived occurrences because no downloadable .trx artifacts were available.");
            }

            if (input.TestQuery is null)
            {
                if (input.ForceNew)
                {
                    warnings.Add("Ignoring --force-new because issue generation requires a specific test query.");
                    Log("Ignoring --force-new because no test query was supplied.");
                }

                var uniqueFailedTestCount = occurrences
                    .Select(static occurrence => (occurrence.CanonicalTestName, occurrence.DisplayTestName))
                    .Distinct()
                    .Count();
                Log($"No test query was supplied; returning all {uniqueFailedTestCount} failing test(s).");
                return CreateAllFailuresResult(
                    input,
                    workflowSection!,
                    resolutionSection!,
                    occurrences,
                    failedJobs,
                    failedTestsByJobUrl,
                    executionLog,
                    warnings,
                    availableFailedTests);
            }

            var matchResult = FailingTestIssueLogic.MatchTestFailures(input.TestQuery, occurrences);
            if (!matchResult.Success || matchResult.CanonicalTestName is null || matchResult.DisplayTestName is null)
            {
                var errorMessage = matchResult.ErrorMessage ?? "The requested test could not be matched.";
                Log(errorMessage);
                return CreateFailureResult(input, workflowSection, resolutionSection, errorMessage, executionLog, warnings, matchResult.CandidateNames.Count > 0 ? matchResult.CandidateNames : availableFailedTests);
            }

            Log($"Matched query using '{matchResult.Strategy}'.");
            var matchingJobs = GetMatchingJobs(matchResult, failedJobs, failedTestsByJobUrl);
            Log($"Matched {matchingJobs.Count} job(s) to the resolved test.");
            GitHubActionsJob? preferredJob = null;

            if (resolvedRun.PreferredJobId is long preferredJobId)
            {
                preferredJob = failedJobs.FirstOrDefault(job => job.Id == preferredJobId);
            }

            var primaryJob = preferredJob ?? matchingJobs.FirstOrDefault();
            if (matchingJobs.Count > 1)
            {
                warnings.Add("The matched test appeared in multiple failed jobs. The primary job was selected from the explicit source job or the first matching job.");
            }
            else if (matchingJobs.Count == 0)
            {
                warnings.Add("The matched test was found in .trx artifacts, but no failed job log mentioned the same test name. Job context is omitted from the occurrence list.");
            }

            var resolvedOccurrences = AttachSingleJobContext(matchResult.Occurrences, matchingJobs);
            var primaryOccurrence = SelectPrimaryOccurrence(resolvedOccurrences, primaryJob);

            if (primaryOccurrence is null)
            {
                const string errorMessage = "The matched test did not produce any resolvable failure occurrences.";
                Log(errorMessage);
                return CreateFailureResult(input, workflowSection, resolutionSection, errorMessage, executionLog, warnings, availableFailedTests);
            }

            Log($"Selected primary occurrence from '{primaryOccurrence.ArtifactName}'.");
            var issueArtifacts = FailingTestIssueLogic.CreateIssueArtifacts(
                workflow: workflowSection,
                resolution: resolutionSection,
                matchResult: matchResult,
                occurrences: resolvedOccurrences,
                primaryOccurrence: primaryOccurrence,
                matchingJobs: matchingJobs.Select(job => (job.Name, job.HtmlUrl)).ToArray(),
                warnings: warnings);

            CreatedIssueInfo? createdIssue = null;
            ExistingIssueInfo? existingIssue = null;
            if (input.Create)
            {
                if (!input.ForceNew)
                {
                    Log("--create flag set. Searching for existing issue...");
                    var existing = await GitHubCli.SearchExistingIssueAsync(
                        input.Repository,
                        issueArtifacts.MetadataMarker,
                        cancellationToken).ConfigureAwait(false);

                    if (existing is not null)
                    {
                        var (existingNumber, existingUrl, existingState) = existing.Value;
                        existingIssue = new ExistingIssueInfo(existingNumber, existingUrl);

                        if (existingState is "closed")
                        {
                            Log($"Found closed issue #{existingNumber}. Reopening...");
                            await GitHubCli.ReopenIssueAsync(input.Repository, existingNumber, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            Log($"Found open issue #{existingNumber}. Adding comment...");
                        }

                        await GitHubCli.AddIssueCommentAsync(
                            input.Repository,
                            existingNumber,
                            issueArtifacts.CommentBody,
                            cancellationToken).ConfigureAwait(false);

                        Log($"Updated existing issue #{existingNumber}: {existingUrl}");
                    }
                }

                if (existingIssue is null)
                {
                    Log(input.ForceNew ? "--force-new flag set. Creating new issue on GitHub..." : "No existing issue found. Creating new issue on GitHub...");
                    var (issueNumber, issueUrl) = await GitHubCli.CreateIssueAsync(
                        input.Repository,
                        issueArtifacts.Title,
                        issueArtifacts.Body,
                        issueArtifacts.Labels,
                        cancellationToken).ConfigureAwait(false);
                    createdIssue = new CreatedIssueInfo(issueNumber, issueUrl);
                    Log($"Created issue #{issueNumber}: {issueUrl}");
                }
            }

            return new CreateFailingTestIssueResult
            {
                Success = true,
                Input = new InputSection(
                    TestQuery: input.TestQuery,
                    SourceUrl: input.SourceUrl,
                    Workflow: workflowSection,
                    ForceNew: input.ForceNew),
                Resolution = resolutionSection,
                Match = new MatchSection(
                    Query: input.TestQuery,
                    Strategy: matchResult.Strategy!,
                    CanonicalTestName: matchResult.CanonicalTestName,
                    DisplayTestName: matchResult.DisplayTestName,
                    AllMatchingOccurrences: resolvedOccurrences
                        .Select(ToMatchedOccurrence)
                        .ToArray()),
                Failure = ToFailureSection(primaryOccurrence, primaryJob),
                Matrix = new MatrixSection(
                    MatchedOccurrences: resolvedOccurrences.Count,
                    Summary: resolvedOccurrences
                        .Select(occurrence => new MatrixSummaryEntry(
                            ArtifactName: occurrence.ArtifactName,
                            ArtifactUrl: occurrence.ArtifactUrl,
                            TrxPath: occurrence.TrxPath,
                            JobName: occurrence.JobName,
                            JobUrl: occurrence.JobUrl))
                        .ToArray()),
                Issue = new IssueSection(
                    Title: issueArtifacts.Title,
                    Labels: issueArtifacts.Labels,
                    StableSignature: issueArtifacts.StableSignature,
                    MetadataMarker: issueArtifacts.MetadataMarker,
                    Body: issueArtifacts.Body,
                    CommentBody: issueArtifacts.CommentBody,
                    ExistingIssue: existingIssue,
                    CreatedIssue: createdIssue),
                Diagnostics = new DiagnosticsSection(
                    Log: executionLog.ToArray(),
                    LogFile: "diagnostics.log",
                    Warnings: warnings.ToArray(),
                    AvailableFailedTests: availableFailedTests)
            };
        }
        catch (Exception ex)
        {
            warnings.Add(ex.Message);
            Log($"Unhandled exception: {ex.Message}");
            return CreateFailureResult(input, workflowSection, resolutionSection, ex.Message, executionLog, warnings, []);
        }
    }

    private static CreateFailingTestIssueResult CreateAllFailuresResult(
        CommandInput input,
        WorkflowSection workflow,
        ResolutionSection resolution,
        IReadOnlyList<FailedTestOccurrence> occurrences,
        IReadOnlyList<GitHubActionsJob> failedJobs,
        IReadOnlyDictionary<string, HashSet<string>> failedTestsByJobUrl,
        IReadOnlyList<string> executionLog,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> availableFailedTests)
    {
        var tests = occurrences
            .GroupBy(
                static occurrence => (occurrence.CanonicalTestName, occurrence.DisplayTestName),
                (key, groupedOccurrences) =>
                {
                    var groupedOccurrenceList = groupedOccurrences.ToArray();
                    var matchingJobs = GetMatchingJobs(key.CanonicalTestName, key.DisplayTestName, failedJobs, failedTestsByJobUrl);
                    var resolvedOccurrences = AttachSingleJobContext(groupedOccurrenceList, matchingJobs);
                    var primaryJob = matchingJobs.Count == 1 ? matchingJobs[0] : null;
                    var primaryOccurrence = SelectPrimaryOccurrence(resolvedOccurrences, primaryJob) ?? resolvedOccurrences[0];

                    return new FailingTestEntry(
                        CanonicalTestName: key.CanonicalTestName,
                        DisplayTestName: key.DisplayTestName,
                        OccurrenceCount: resolvedOccurrences.Count,
                        Occurrences: resolvedOccurrences.Select(ToMatchedOccurrence).ToArray(),
                        PrimaryFailure: ToFailureSection(primaryOccurrence, primaryJob));
                })
            .OrderBy(static test => test.CanonicalTestName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static test => test.DisplayTestName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CreateFailingTestIssueResult
        {
            Success = true,
            Input = new InputSection(
                TestQuery: input.TestQuery,
                SourceUrl: input.SourceUrl,
                Workflow: workflow,
                ForceNew: input.ForceNew),
            Resolution = resolution,
            AllFailures = new AllFailuresSection(
                FailedTests: tests.Length,
                Tests: tests),
            Diagnostics = new DiagnosticsSection(
                Log: executionLog.ToArray(),
                LogFile: "diagnostics.log",
                Warnings: warnings.ToArray(),
                AvailableFailedTests: availableFailedTests)
        };
    }

    private static CreateFailingTestIssueResult CreateFailureResult(
        CommandInput input,
        WorkflowSection? workflow,
        ResolutionSection? resolution,
        string errorMessage,
        IReadOnlyList<string> executionLog,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> availableFailedTests)
    {
        return new CreateFailingTestIssueResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            Input = new InputSection(
                TestQuery: input.TestQuery,
                SourceUrl: input.SourceUrl,
                Workflow: workflow ?? new WorkflowSection(
                    Requested: input.WorkflowSelector,
                    ResolvedWorkflowFile: string.Empty,
                    ResolvedWorkflowName: string.Empty),
                ForceNew: input.ForceNew),
            Resolution = resolution,
            Diagnostics = new DiagnosticsSection(
                Log: executionLog.ToArray(),
                LogFile: "diagnostics.log",
                Warnings: warnings.ToArray(),
                AvailableFailedTests: availableFailedTests)
        };
    }

    private static async Task<WorkflowMetadata> ResolveWorkflowAsync(string repository, string workflowFile, CancellationToken cancellationToken)
    {
        using var workflowDocument = await GitHubCli.GetJsonAsync(
            $"repos/{repository}/actions/workflows/{Path.GetFileName(workflowFile)}",
            cancellationToken).ConfigureAwait(false);

        var root = workflowDocument.RootElement;
        return new WorkflowMetadata(
            Id: root.GetProperty("id").GetInt64(),
            Name: root.GetProperty("name").GetString() ?? Path.GetFileName(workflowFile),
            Path: root.GetProperty("path").GetString() ?? workflowFile);
    }

    private static async Task<ResolvedRun> ResolveRunAsync(
        string repository,
        WorkflowMetadata workflow,
        SourceUrlResolution sourceResolution,
        CancellationToken cancellationToken)
    {
        return sourceResolution.SourceKind switch
        {
            "pull_request" => await ResolveRunFromPullRequestAsync(repository, workflow, sourceResolution.PullRequestNumber!.Value, cancellationToken).ConfigureAwait(false),
            "run" => await ResolveRunFromRunIdAsync(repository, workflow, sourceResolution.RunId!.Value, sourceResolution.RunAttempt, null, cancellationToken).ConfigureAwait(false),
            "job" => await ResolveRunFromJobIdAsync(repository, workflow, sourceResolution.JobId!.Value, sourceResolution.RunId, sourceResolution.RunAttempt, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported source kind '{sourceResolution.SourceKind}'.")
        };
    }

    private static async Task<ResolvedRun> ResolveRunFromPullRequestAsync(string repository, WorkflowMetadata workflow, int pullRequestNumber, CancellationToken cancellationToken)
    {
        using var pullRequestDocument = await GitHubCli.GetJsonAsync(
            $"repos/{repository}/pulls/{pullRequestNumber}",
            cancellationToken).ConfigureAwait(false);

        var root = pullRequestDocument.RootElement;
        var headSha = root.GetProperty("head").GetProperty("sha").GetString();
        if (string.IsNullOrWhiteSpace(headSha))
        {
            throw new InvalidOperationException($"Pull request #{pullRequestNumber} does not have a resolvable head SHA.");
        }

        // Search all runs (not just completed) to find in-progress reruns and the latest matching run.
        // Prefer completed runs, but fall back to in-progress/queued runs if no completed run is found.
        JsonElement? bestNonCompletedRun = null;

        for (var page = 1; ; page++)
        {
            using var runsDocument = await GitHubCli.GetJsonAsync(
                $"repos/{repository}/actions/workflows/{Path.GetFileName(workflow.Path)}/runs?per_page=100&page={page}",
                cancellationToken).ConfigureAwait(false);

            var runs = runsDocument.RootElement.GetProperty("workflow_runs");
            if (runs.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var run in runs.EnumerateArray())
            {
                if (run.GetProperty("workflow_id").GetInt64() != workflow.Id)
                {
                    continue;
                }

                var runPullRequestNumbers = GetPullRequestNumbers(run);
                var runHeadSha = run.TryGetProperty("head_sha", out var headShaElement) ? headShaElement.GetString() : null;
                if (!runPullRequestNumbers.Contains(pullRequestNumber) && !string.Equals(runHeadSha, headSha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var status = run.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                if (status is "completed")
                {
                    return CreateResolvedRun(run, pullRequestNumber, preferredJobId: null, requestedRunAttempt: null);
                }

                // Remember the first non-completed match as a fallback
                bestNonCompletedRun ??= run.Clone();
            }

            if (runs.GetArrayLength() < 100)
            {
                break;
            }
        }

        if (bestNonCompletedRun is not null)
        {
            return CreateResolvedRun(bestNonCompletedRun.Value, pullRequestNumber, preferredJobId: null, requestedRunAttempt: null);
        }

        throw new InvalidOperationException($"No workflow run for '{workflow.Name}' was found for pull request #{pullRequestNumber}.");
    }

    private static async Task<ResolvedRun> ResolveRunFromRunIdAsync(
        string repository,
        WorkflowMetadata workflow,
        long runId,
        int? requestedRunAttempt,
        long? preferredJobId,
        CancellationToken cancellationToken)
    {
        using var runDocument = await GitHubCli.GetJsonAsync(
            $"repos/{repository}/actions/runs/{runId}",
            cancellationToken).ConfigureAwait(false);

        var root = runDocument.RootElement;
        if (root.GetProperty("workflow_id").GetInt64() != workflow.Id)
        {
            var actualWorkflowName = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            throw new InvalidOperationException(
                $"Run {runId} belongs to workflow '{actualWorkflowName ?? "unknown"}', not '{workflow.Name}'.");
        }

        var pullRequestNumbers = GetPullRequestNumbers(root);
        return CreateResolvedRun(
            root,
            pullRequestNumbers.FirstOrDefault(defaultValue: 0) is var number && number > 0 ? number : null,
            preferredJobId,
            requestedRunAttempt);
    }

    private static async Task<ResolvedRun> ResolveRunFromJobIdAsync(
        string repository,
        WorkflowMetadata workflow,
        long jobId,
        long? runIdHint,
        int? requestedRunAttempt,
        CancellationToken cancellationToken)
    {
        using var jobDocument = await GitHubCli.GetJsonAsync(
            $"repos/{repository}/actions/jobs/{jobId}",
            cancellationToken).ConfigureAwait(false);

        var root = jobDocument.RootElement;
        var runId = root.TryGetProperty("run_id", out var runIdProperty)
            ? runIdProperty.GetInt64()
            : runIdHint ?? throw new InvalidOperationException($"Job {jobId} does not include a run id.");

        return await ResolveRunFromRunIdAsync(repository, workflow, runId, requestedRunAttempt, jobId, cancellationToken).ConfigureAwait(false);
    }

    private static ResolvedRun CreateResolvedRun(JsonElement run, int? pullRequestNumber, long? preferredJobId, int? requestedRunAttempt)
    {
        var actualRunAttempt = run.TryGetProperty("run_attempt", out var runAttemptElement) ? runAttemptElement.GetInt32() : 1;
        var effectiveRunAttempt = requestedRunAttempt ?? actualRunAttempt;
        if (effectiveRunAttempt <= 0)
        {
            throw new InvalidOperationException($"Workflow run {run.GetProperty("id").GetInt64()} has an invalid attempt number '{effectiveRunAttempt}'.");
        }

        if (requestedRunAttempt is not null && requestedRunAttempt > actualRunAttempt)
        {
            throw new InvalidOperationException(
                $"Workflow run {run.GetProperty("id").GetInt64()} does not have attempt {requestedRunAttempt.Value}.");
        }

        return new ResolvedRun(
            RunId: run.GetProperty("id").GetInt64(),
            RunUrl: BuildRunUrl(run.GetProperty("html_url").GetString() ?? string.Empty, requestedRunAttempt),
            RunAttempt: effectiveRunAttempt,
            Status: run.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? string.Empty : string.Empty,
            PullRequestNumber: pullRequestNumber,
            PreferredJobId: preferredJobId,
            IsSpecificAttempt: requestedRunAttempt is not null);
    }

    private static async Task<List<GitHubActionsJob>> ListFailedJobsAsync(string repository, ResolvedRun resolvedRun, CancellationToken cancellationToken)
    {
        var jobs = await GitHubActionsApi.ListJobsAsync(
            repository,
            resolvedRun.RunId,
            resolvedRun.IsSpecificAttempt ? resolvedRun.RunAttempt : null,
            cancellationToken).ConfigureAwait(false);

        return jobs
            .Where(job => !string.IsNullOrWhiteSpace(job.Conclusion) && s_failedConclusions.Contains(job.Conclusion))
            .ToList();
    }

    private static string BuildRunUrl(string runUrl, int? requestedRunAttempt)
    {
        if (string.IsNullOrWhiteSpace(runUrl) || requestedRunAttempt is null)
        {
            return runUrl;
        }

        return runUrl.TrimEnd('/') + $"/attempts/{requestedRunAttempt.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task<List<JobLogData>> DownloadFailedTestsFromLogsAsync(
        string repository,
        IReadOnlyList<GitHubActionsJob> jobs,
        List<string> warnings,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        List<JobLogData> results = [];

        foreach (var job in jobs)
        {
            string logs;
            try
            {
                logs = await GitHubActionsApi.DownloadJobLogAsync(repository, job.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsMissingJobLog(ex))
            {
                log?.Invoke($"Skipping missing log for failed job '{job.Name}' ({job.Id}).");
                warnings?.Add($"Failed job log was unavailable for '{job.Name}' ({job.Id}); continuing without it.");
                continue;
            }

            var occurrences = ParseFailedTestOccurrencesFromLog(job, logs);
            var failedTests = occurrences
                .SelectMany(static occurrence => new[] { occurrence.CanonicalTestName, occurrence.DisplayTestName })
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in s_failedTestPattern.Matches(logs)
                .Select(match => match.Groups["name"].Value.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                failedTests.Add(name);
            }

            results.Add(new JobLogData(job, failedTests, occurrences));
            log?.Invoke($"Loaded failed job log '{job.Name}' and found {failedTests.Count} failed test name(s) with {occurrences.Count} detailed occurrence(s).");
        }

        return results;
    }

    private static bool IsMissingJobLog(InvalidOperationException ex)
        => ex.Message.Contains("/logs", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase);

    private static List<FailedTestOccurrence> ParseFailedTestOccurrencesFromLog(GitHubActionsJob job, string logs)
    {
        var normalizedLines = logs
            .Split('\n')
            .Select(static line => s_logPrefixPattern.Replace(line.TrimEnd('\r'), string.Empty))
            .ToArray();

        var summaryOccurrences = ParseSummaryOccurrences(job, normalizedLines);
        if (summaryOccurrences.Count > 0)
        {
            return summaryOccurrences;
        }

        return ParseDirectFailureOccurrences(job, normalizedLines);
    }

    private static List<FailedTestOccurrence> ParseSummaryOccurrences(GitHubActionsJob job, IReadOnlyList<string> lines)
    {
        List<FailedTestOccurrence> occurrences = [];

        for (var i = 0; i < lines.Count; i++)
        {
            var match = s_logSummaryPattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var displayName = WebUtility.HtmlDecode(match.Groups["name"].Value.Trim());
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var fenceStart = FindNextFenceLine(lines, i + 1);
            if (fenceStart < 0)
            {
                continue;
            }

            var fenceEnd = FindNextFenceLine(lines, fenceStart + 1);
            if (fenceEnd < 0)
            {
                continue;
            }

            var details = lines
                .Skip(fenceStart + 1)
                .Take(fenceEnd - fenceStart - 1)
                .ToArray();

            var (errorMessage, stackTrace) = SplitErrorAndStack(details);
            occurrences.Add(CreateLogOccurrence(job, displayName, errorMessage, stackTrace));
            i = fenceEnd;
        }

        return occurrences;
    }

    private static List<FailedTestOccurrence> ParseDirectFailureOccurrences(GitHubActionsJob job, IReadOnlyList<string> lines)
    {
        List<FailedTestOccurrence> occurrences = [];

        for (var i = 0; i < lines.Count; i++)
        {
            var match = s_directFailedLinePattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var displayName = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            List<string> details = [];
            for (var detailIndex = i + 1; detailIndex < lines.Count; detailIndex++)
            {
                var detailLine = lines[detailIndex];
                var trimmedDetailLine = detailLine.Trim();

                if (string.IsNullOrWhiteSpace(trimmedDetailLine))
                {
                    if (details.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (s_directFailedLinePattern.IsMatch(detailLine) || s_logSummaryPattern.IsMatch(detailLine))
                {
                    break;
                }

                if (!detailLine.StartsWith(" ", StringComparison.Ordinal) && !IsFailureDetailLine(trimmedDetailLine))
                {
                    if (details.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                details.Add(trimmedDetailLine);
            }

            var (errorMessage, stackTrace) = SplitErrorAndStack(details);
            occurrences.Add(CreateLogOccurrence(job, displayName, errorMessage, stackTrace));
        }

        return occurrences;
    }

    private static FailedTestOccurrence CreateLogOccurrence(GitHubActionsJob job, string displayName, string errorMessage, string stackTrace)
        => new(
            CanonicalTestName: GetCanonicalTestName(displayName),
            DisplayTestName: displayName,
            ArtifactName: "job log",
            TrxPath: "n/a",
            ErrorMessage: errorMessage,
            StackTrace: stackTrace,
            Stdout: string.Empty,
            JobName: job.Name,
            JobUrl: job.HtmlUrl);

    private static int FindNextFenceLine(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static (string ErrorMessage, string StackTrace) SplitErrorAndStack(IReadOnlyList<string> lines)
    {
        List<string> errorLines = [];
        List<string> stackLines = [];

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var inlineStackMatch = s_inlineStackPattern.Match(line);
            if (inlineStackMatch.Success)
            {
                var error = inlineStackMatch.Groups["error"].Value.TrimEnd();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    errorLines.Add(error);
                }

                stackLines.Add(inlineStackMatch.Groups["stack"].Value.Trim());
                continue;
            }

            if (IsStackTraceLine(line))
            {
                stackLines.Add(line.Trim());
                continue;
            }

            if (stackLines.Count == 0)
            {
                errorLines.Add(line.TrimEnd());
            }
            else
            {
                stackLines.Add(line.Trim());
            }
        }

        return (string.Join(Environment.NewLine, errorLines).Trim(), string.Join(Environment.NewLine, stackLines).Trim());
    }

    private static bool IsStackTraceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("at ", StringComparison.Ordinal)
            || trimmed.StartsWith("--- End of stack trace", StringComparison.Ordinal);
    }

    private static bool IsFailureDetailLine(string line)
        => line.StartsWith("Assert.", StringComparison.Ordinal)
            || line.StartsWith("Expected:", StringComparison.Ordinal)
            || line.StartsWith("Actual:", StringComparison.Ordinal)
            || line.StartsWith("Error Message:", StringComparison.Ordinal)
            || IsStackTraceLine(line);

    private static string GetCanonicalTestName(string displayName)
    {
        var parameterStart = displayName.IndexOf('(');
        return parameterStart > 0 ? displayName[..parameterStart] : displayName;
    }

    private static async Task<List<FailedTestOccurrence>> DownloadFailedTestOccurrencesAsync(
        string repository,
        long runId,
        bool allowArtifactFallback,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var artifacts = await GitHubActionsApi.ListArtifactsAsync(repository, runId, cancellationToken).ConfigureAwait(false);

        log?.Invoke($"Enumerated {artifacts.Count} artifact(s) for workflow run {runId}.");

        if (artifacts.Count == 0)
        {
            return [];
        }

        var candidateArtifacts = artifacts.Where(static artifact => MayContainTrxFiles(artifact.Name)).ToList();
        log?.Invoke($"Identified {candidateArtifacts.Count} artifact(s) whose names suggest test results.");
        if (candidateArtifacts.Count == 0)
        {
            if (!allowArtifactFallback)
            {
                log?.Invoke("Skipping fallback to all artifacts because the run is still active and no likely test-results artifacts were found.");
                return [];
            }

            log?.Invoke("Falling back to all artifacts because no artifact names matched likely test-results patterns.");
            candidateArtifacts = artifacts;
        }

        var tempRoot = Directory.CreateTempSubdirectory("aspire-failing-test-issue").FullName;

        try
        {
            List<FailedTestOccurrence> occurrences = [];

            foreach (var artifact in candidateArtifacts)
            {
                log?.Invoke($"Downloading artifact '{artifact.Name}' ({artifact.Id}).");
                var zipPath = Path.Combine(tempRoot, $"{artifact.Id}.zip");
                await GitHubActionsApi.DownloadArtifactZipAsync(
                    repository,
                    artifact.Id,
                    zipPath,
                    cancellationToken).ConfigureAwait(false);

                var extractDirectory = Path.Combine(tempRoot, artifact.Id.ToString(CultureInfo.InvariantCulture));
                ValidateZipEntries(zipPath, extractDirectory);
                ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);

                foreach (var trxPath in Directory.EnumerateFiles(extractDirectory, "*.trx", SearchOption.AllDirectories))
                {
                    var relativeTrxPath = Path.GetRelativePath(extractDirectory, trxPath).Replace('\\', '/');
                    var results = TrxReader.GetDetailedTestResultsFromTrx(
                        trxPath,
                        static result => s_failedOutcomes.Contains(result.Outcome));

                    occurrences.AddRange(results.Select(result => new FailedTestOccurrence(
                        CanonicalTestName: result.CanonicalName,
                        DisplayTestName: result.DisplayName,
                        ArtifactName: artifact.Name,
                        TrxPath: relativeTrxPath,
                        ErrorMessage: result.ErrorMessage ?? string.Empty,
                        StackTrace: result.StackTrace ?? string.Empty,
                        Stdout: result.Stdout ?? string.Empty,
                        ArtifactUrl: $"https://github.com/{repository}/actions/runs/{runId}/artifacts/{artifact.Id}")));
                }
            }

            log?.Invoke($"Extracted {occurrences.Count} failed test occurrence(s) from downloaded artifacts.");

            return occurrences;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static bool MayContainTrxFiles(string artifactName)
        => artifactName.Contains("TestResults", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetAvailableFailedTests(IReadOnlyCollection<FailedTestOccurrence> occurrences)
    {
        return occurrences
            .Select(static occurrence => FailingTestIssueLogic.FormatCandidateName(occurrence.CanonicalTestName, occurrence.DisplayTestName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
    }

    private static List<GitHubActionsJob> GetMatchingJobs(
        FailureMatchResult matchResult,
        IReadOnlyList<GitHubActionsJob> failedJobs,
        IReadOnlyDictionary<string, HashSet<string>> failedTestsByJobUrl)
        => GetMatchingJobs(matchResult.CanonicalTestName, matchResult.DisplayTestName, failedJobs, failedTestsByJobUrl);

    private static List<GitHubActionsJob> GetMatchingJobs(
        string? canonicalTestName,
        string? displayTestName,
        IReadOnlyList<GitHubActionsJob> failedJobs,
        IReadOnlyDictionary<string, HashSet<string>> failedTestsByJobUrl)
    {
        List<GitHubActionsJob> jobs = [];

        foreach (var job in failedJobs)
        {
            if (string.IsNullOrWhiteSpace(job.HtmlUrl))
            {
                continue;
            }

            if (!failedTestsByJobUrl.TryGetValue(job.HtmlUrl, out var failedTests))
            {
                continue;
            }

            if (failedTests.Any(test => string.Equals(test, canonicalTestName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(test, displayTestName, StringComparison.OrdinalIgnoreCase)))
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    private static IReadOnlyList<FailedTestOccurrence> AttachSingleJobContext(
        IReadOnlyList<FailedTestOccurrence> occurrences,
        IReadOnlyList<GitHubActionsJob> matchingJobs)
    {
        if (matchingJobs.Count != 1)
        {
            return occurrences;
        }

        var job = matchingJobs[0];
        return occurrences
            .Select(occurrence => occurrence with
            {
                JobName = job.Name,
                JobUrl = job.HtmlUrl
            })
            .ToArray();
    }

    private static FailedTestOccurrence? SelectPrimaryOccurrence(
        IReadOnlyList<FailedTestOccurrence> occurrences,
        GitHubActionsJob? primaryJob)
    {
        if (occurrences.Count == 0)
        {
            return null;
        }

        if (primaryJob is not null)
        {
            var byJobUrl = occurrences.FirstOrDefault(occurrence => string.Equals(occurrence.JobUrl, primaryJob.HtmlUrl, StringComparison.OrdinalIgnoreCase));
            if (byJobUrl is not null)
            {
                return byJobUrl;
            }
        }

        return occurrences[0];
    }

    private static MatchedOccurrence ToMatchedOccurrence(FailedTestOccurrence occurrence)
        => new(
            CanonicalTestName: occurrence.CanonicalTestName,
            DisplayTestName: occurrence.DisplayTestName,
            ArtifactName: occurrence.ArtifactName,
            ArtifactUrl: occurrence.ArtifactUrl,
            TrxPath: occurrence.TrxPath,
            JobName: occurrence.JobName,
            JobUrl: occurrence.JobUrl);

    private static FailureSection ToFailureSection(FailedTestOccurrence occurrence, GitHubActionsJob? primaryJob)
        => new(
            ErrorMessage: occurrence.ErrorMessage,
            StackTrace: occurrence.StackTrace,
            Stdout: occurrence.Stdout,
            ArtifactName: occurrence.ArtifactName,
            ArtifactUrl: occurrence.ArtifactUrl,
            TrxPath: occurrence.TrxPath,
            JobName: primaryJob?.Name ?? occurrence.JobName,
            JobUrl: primaryJob?.HtmlUrl ?? occurrence.JobUrl);

    private static List<int> GetPullRequestNumbers(JsonElement run)
    {
        if (!run.TryGetProperty("pull_requests", out var pullRequestsElement) || pullRequestsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return pullRequestsElement.EnumerateArray()
            .Where(static pullRequest => pullRequest.TryGetProperty("number", out _))
            .Select(static pullRequest => pullRequest.GetProperty("number").GetInt32())
            .Distinct()
            .ToList();
    }

    private sealed record WorkflowMetadata(
        long Id,
        string Name,
        string Path);

    private sealed record ResolvedRun(
        long RunId,
        string RunUrl,
        int RunAttempt,
        string Status,
        int? PullRequestNumber,
        long? PreferredJobId,
        bool IsSpecificAttempt);

    private sealed record JobLogData(
        GitHubActionsJob Job,
        HashSet<string> FailedTests,
        IReadOnlyList<FailedTestOccurrence> Occurrences);

    private static void ValidateZipEntries(string zipPath, string extractDirectory)
    {
        var fullExtractPath = Path.GetFullPath(extractDirectory) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(extractDirectory, entry.FullName));
            if (!destinationPath.StartsWith(fullExtractPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip entry '{entry.FullName}' would extract outside the target directory.");
            }
        }
    }
}

public static class FailingTestIssueLogic
{
    private const string DefaultWorkflowSelector = "ci";
    private const int ErrorMessageCommentBudget = 4_000;
    private const int IssueBodyMaxLength = 58 * 1024;
    private const int MinimumErrorSectionLength = 512;
    private const int MinimumStackSectionLength = 512;
    private const int MinimumStdoutSectionLength = 512;
    private const int ErrorDetailsCollapsibleLineThreshold = 30;
    private const string IssueSizeTruncationNote = "Snipped in the middle to keep the issue body under GitHub's 64 KB limit.";
    private static readonly IReadOnlyDictionary<string, string> s_workflowAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ci"] = ".github/workflows/ci.yml"
    };

    private static readonly Regex s_pullRequestUrlPattern = new(@"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<number>\d+)(?:/.*)?(?:\?.*)?(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_jobUrlPattern = new(@"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/actions/runs/(?<runId>\d+)(?:/attempts/(?<attempt>\d+))?/job/(?<jobId>\d+)/?(?:\?.*)?(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_runUrlPattern = new(@"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/actions/runs/(?<runId>\d+)(?:/attempts/(?<attempt>\d+))?/?(?:\?.*)?(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex s_rawWorkflowPathPattern = new(@"^\.github/workflows/[^/\\]+\.(?:yml|yaml)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static WorkflowSelectorResolution ResolveWorkflowSelector(string? selector)
    {
        var requestedSelector = string.IsNullOrWhiteSpace(selector) ? DefaultWorkflowSelector : selector.Trim();

        if (s_workflowAliases.TryGetValue(requestedSelector, out var aliasedWorkflow))
        {
            return new WorkflowSelectorResolution(true, requestedSelector, aliasedWorkflow, null);
        }

        if (s_rawWorkflowPathPattern.IsMatch(requestedSelector.Replace('\\', '/')))
        {
            return new WorkflowSelectorResolution(true, requestedSelector, requestedSelector.Replace('\\', '/'), null);
        }

        return new WorkflowSelectorResolution(
            Success: false,
            Requested: requestedSelector,
            ResolvedWorkflowFile: null,
            ErrorMessage: $"Unknown workflow selector '{requestedSelector}'. Use the 'ci' alias or a .github/workflows/*.yml path.");
    }

    public static SourceUrlResolution ParseSourceUrl(string? sourceUrl, string repository)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new SourceUrlResolution(false, null, null, null, null, null, "A PR, workflow run, or workflow job URL is required.");
        }

        var value = sourceUrl.Trim();

        var jobMatch = s_jobUrlPattern.Match(value);
        if (jobMatch.Success)
        {
            if (!MatchesRepository(repository, jobMatch.Groups["owner"].Value, jobMatch.Groups["repo"].Value))
            {
                return new SourceUrlResolution(false, null, null, null, null, null, $"The source URL repository does not match '{repository}'.");
            }

            return new SourceUrlResolution(
                Success: true,
                SourceKind: "job",
                PullRequestNumber: null,
                RunId: long.Parse(jobMatch.Groups["runId"].Value, CultureInfo.InvariantCulture),
                RunAttempt: ParseOptionalInt(jobMatch.Groups["attempt"].Value),
                JobId: long.Parse(jobMatch.Groups["jobId"].Value, CultureInfo.InvariantCulture),
                ErrorMessage: null);
        }

        var runMatch = s_runUrlPattern.Match(value);
        if (runMatch.Success)
        {
            if (!MatchesRepository(repository, runMatch.Groups["owner"].Value, runMatch.Groups["repo"].Value))
            {
                return new SourceUrlResolution(false, null, null, null, null, null, $"The source URL repository does not match '{repository}'.");
            }

            return new SourceUrlResolution(
                Success: true,
                SourceKind: "run",
                PullRequestNumber: null,
                RunId: long.Parse(runMatch.Groups["runId"].Value, CultureInfo.InvariantCulture),
                RunAttempt: ParseOptionalInt(runMatch.Groups["attempt"].Value),
                JobId: null,
                ErrorMessage: null);
        }

        var pullRequestMatch = s_pullRequestUrlPattern.Match(value);
        if (pullRequestMatch.Success)
        {
            if (!MatchesRepository(repository, pullRequestMatch.Groups["owner"].Value, pullRequestMatch.Groups["repo"].Value))
            {
                return new SourceUrlResolution(false, null, null, null, null, null, $"The source URL repository does not match '{repository}'.");
            }

            return new SourceUrlResolution(
                Success: true,
                SourceKind: "pull_request",
                PullRequestNumber: int.Parse(pullRequestMatch.Groups["number"].Value, CultureInfo.InvariantCulture),
                RunId: null,
                RunAttempt: null,
                JobId: null,
                ErrorMessage: null);
        }

        return new SourceUrlResolution(false, null, null, null, null, null, "The source URL must be a GitHub pull request, workflow run, or workflow job URL.");
    }

    public static FailureMatchResult MatchTestFailures(string query, IReadOnlyCollection<FailedTestOccurrence> occurrences)
    {
        var normalizedQuery = NormalizeName(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new FailureMatchResult(false, "A non-empty test query is required.", null, null, null, [], []);
        }

        var groups = occurrences
            .GroupBy(static occurrence => new MatchGroupKey(
                CanonicalName: NormalizeName(occurrence.CanonicalTestName),
                DisplayName: NormalizeName(occurrence.DisplayTestName)))
            .Select(static group => new MatchGroup(group.Key, group.ToArray()))
            .ToArray();

        var exactCanonicalGroups = groups.Where(group => string.Equals(group.Key.CanonicalName, normalizedQuery, StringComparison.Ordinal)).ToArray();
        if (exactCanonicalGroups.Length == 1)
        {
            return CreateSuccessfulMatch("exactCanonical", exactCanonicalGroups[0].Occurrences);
        }

        if (exactCanonicalGroups.Length > 1)
        {
            return CreateFailedMatch($"The query '{query}' matched multiple canonical test names.", exactCanonicalGroups);
        }

        var exactDisplayGroups = groups.Where(group => string.Equals(group.Key.DisplayName, normalizedQuery, StringComparison.Ordinal)).ToArray();
        if (exactDisplayGroups.Length == 1)
        {
            return CreateSuccessfulMatch("exactDisplay", exactDisplayGroups[0].Occurrences);
        }

        if (exactDisplayGroups.Length > 1)
        {
            return CreateFailedMatch($"The query '{query}' matched multiple display test names.", exactDisplayGroups);
        }

        var partialGroups = groups.Where(group =>
                group.Key.CanonicalName.Contains(normalizedQuery, StringComparison.Ordinal)
                || group.Key.DisplayName.Contains(normalizedQuery, StringComparison.Ordinal))
            .ToArray();

        if (partialGroups.Length == 1)
        {
            return CreateSuccessfulMatch("uniqueCaseInsensitiveContains", partialGroups[0].Occurrences);
        }

        if (partialGroups.Length > 1)
        {
            return CreateFailedMatch($"The query '{query}' is ambiguous. Use a more specific canonical or display test name.", partialGroups);
        }

        var candidates = occurrences
            .Select(static occurrence => FormatCandidateName(occurrence.CanonicalTestName, occurrence.DisplayTestName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        return new FailureMatchResult(
            Success: false,
            ErrorMessage: $"The query '{query}' did not match any failed tests in the available .trx artifacts.",
            Strategy: null,
            CanonicalTestName: null,
            DisplayTestName: null,
            Occurrences: [],
            CandidateNames: candidates);
    }

    public static string ComputeStableSignature(string canonicalTestName, string workflowFile)
    {
        var normalizedTestName = NormalizeName(canonicalTestName);
        var normalizedWorkflowFile = workflowFile.Trim().Replace('\\', '/').ToLowerInvariant();
        var payload = $"{normalizedTestName}|{normalizedWorkflowFile}";
        var hash = XxHash3.Hash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    public static TruncationResult TruncateContent(string? content, int maxChars, TruncationPreference preference)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new TruncationResult(string.Empty, false, null);
        }

        if (maxChars <= 0 || content.Length <= maxChars)
        {
            return new TruncationResult(content, false, null);
        }

        return preference switch
        {
            TruncationPreference.Start => new TruncationResult(content[..maxChars], true, $"Truncated to the first {maxChars:N0} characters."),
            TruncationPreference.Middle => new TruncationResult(
                SnipMiddle(content, maxChars),
                true,
                $"Snipped in the middle to {maxChars:N0} characters."),
            TruncationPreference.End => new TruncationResult(content[^maxChars..], true, $"Truncated to the last {maxChars:N0} characters."),
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
    }

    public static IssueArtifacts CreateIssueArtifacts(
        WorkflowSection workflow,
        ResolutionSection resolution,
        FailureMatchResult matchResult,
        IReadOnlyList<FailedTestOccurrence> occurrences,
        FailedTestOccurrence primaryOccurrence,
        IReadOnlyList<(string Name, string Url)> matchingJobs,
        IReadOnlyList<string> warnings)
    {
        var stableSignature = ComputeStableSignature(matchResult.CanonicalTestName!, workflow.ResolvedWorkflowFile);
        var metadataMarker = $"<!-- failing-test-signature: v1:{stableSignature} -->";
        var title = $"[Failing test]: {matchResult.DisplayTestName}";
        var testFailingLine = EscapeMarkdownInline(matchResult.CanonicalTestName ?? matchResult.DisplayTestName!);

        var errorTemplateMessage = CreateKnownIssueErrorMessage(primaryOccurrence.ErrorMessage);

        var errorResult = new TruncationResult(primaryOccurrence.ErrorMessage, false, null);
        var stackTraceResult = new TruncationResult(primaryOccurrence.StackTrace, false, null);
        var stdoutResult = new TruncationResult(primaryOccurrence.Stdout, false, null);

        var body = BuildIssueBody(
            resolution,
            metadataMarker,
            testFailingLine,
            primaryOccurrence,
            errorTemplateMessage,
            errorResult,
            stackTraceResult,
            stdoutResult);

        if (body.Length > IssueBodyMaxLength)
        {
            ShrinkSectionUntilWithinLimit(ref stdoutResult, MinimumStdoutSectionLength, () => BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult));

            body = BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult);
        }

        if (body.Length > IssueBodyMaxLength)
        {
            ShrinkSectionUntilWithinLimit(ref stackTraceResult, MinimumStackSectionLength, () => BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult));

            body = BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult);
        }

        if (body.Length > IssueBodyMaxLength)
        {
            ShrinkSectionUntilWithinLimit(ref errorResult, MinimumErrorSectionLength, () => BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult));

            body = BuildIssueBody(
                resolution,
                metadataMarker,
                testFailingLine,
                primaryOccurrence,
                errorTemplateMessage,
                errorResult,
                stackTraceResult,
                stdoutResult);
        }

        StringBuilder commentBuilder = new();
        AppendInvariantLine(commentBuilder, $"Another failure for `{EscapeBacktickContent(matchResult.DisplayTestName)}` was resolved from {ToMarkdownLink(resolution.RunUrl)}.");
        commentBuilder.AppendLine();
        AppendInvariantLine(commentBuilder, $"- Artifact: `{EscapeBacktickContent(primaryOccurrence.ArtifactName)}`");
        AppendInvariantLine(commentBuilder, $"- `.trx`: `{EscapeBacktickContent(primaryOccurrence.TrxPath)}`");
        if (!string.IsNullOrWhiteSpace(primaryOccurrence.JobName))
        {
            AppendInvariantLine(commentBuilder, $"- Job: {ToMarkdownLink(primaryOccurrence.JobUrl, primaryOccurrence.JobName)}");
        }

        commentBuilder.AppendLine();
        var commentErrorResult = TruncateContent(primaryOccurrence.ErrorMessage, ErrorMessageCommentBudget, TruncationPreference.Start);
        AppendCodeSection(commentBuilder, "Error message", commentErrorResult.Content, commentErrorResult.Note);

        return new IssueArtifacts(
            Title: title,
            Labels: ["failing-test"],
            StableSignature: stableSignature,
            MetadataMarker: metadataMarker,
            Body: body.TrimEnd(),
            CommentBody: commentBuilder.ToString().TrimEnd());
    }

    public static string FormatCandidateName(string canonicalTestName, string displayTestName)
    {
        if (string.Equals(canonicalTestName, displayTestName, StringComparison.OrdinalIgnoreCase))
        {
            return canonicalTestName;
        }

        return $"{canonicalTestName} | {displayTestName}";
    }

    private static string NormalizeName(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static bool MatchesRepository(string repository, string owner, string repo)
    {
        var split = repository.Split('/', count: 2);
        return split.Length == 2
            && string.Equals(split[0], owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(split[1], repo, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseOptionalInt(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : null;

    private static FailureMatchResult CreateSuccessfulMatch(string strategy, IReadOnlyList<FailedTestOccurrence> occurrences)
    {
        var first = occurrences[0];
        return new FailureMatchResult(
            Success: true,
            ErrorMessage: null,
            Strategy: strategy,
            CanonicalTestName: first.CanonicalTestName,
            DisplayTestName: first.DisplayTestName,
            Occurrences: occurrences,
            CandidateNames: []);
    }

    private static FailureMatchResult CreateFailedMatch(string errorMessage, IReadOnlyCollection<MatchGroup> groups)
    {
        var candidates = groups
            .Select(static group => FormatCandidateName(group.Occurrences[0].CanonicalTestName, group.Occurrences[0].DisplayTestName))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FailureMatchResult(
            Success: false,
            ErrorMessage: errorMessage,
            Strategy: null,
            CanonicalTestName: null,
            DisplayTestName: null,
            Occurrences: [],
            CandidateNames: candidates);
    }

    private static void AppendCodeSection(StringBuilder builder, string heading, string content, string? truncationNote)
    {
        AppendInvariantLine(builder, $"#### {heading}");
        builder.AppendLine();

        if (string.IsNullOrEmpty(content))
        {
            builder.AppendLine("_No content captured._");
            builder.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(truncationNote))
        {
            AppendInvariantLine(builder, $"_{truncationNote}_");
            builder.AppendLine();
        }

        var fence = GetMarkdownFence(content);
        AppendInvariantLine(builder, $"{fence}text");
        builder.AppendLine(content);
        builder.AppendLine(fence);
        builder.AppendLine();
    }

    private static string BuildIssueBody(
        ResolutionSection resolution,
        string metadataMarker,
        string testFailingLine,
        FailedTestOccurrence primaryOccurrence,
        string errorTemplateMessage,
        TruncationResult errorResult,
        TruncationResult stackTraceResult,
        TruncationResult stdoutResult)
    {
        StringBuilder bodyBuilder = new();

        bodyBuilder.AppendLine("### Build information");
        bodyBuilder.AppendLine();
        AppendInvariantLine(bodyBuilder, $"Build: {resolution.RunUrl}");
        bodyBuilder.Append("Build error leg or test failing: ");
        bodyBuilder.AppendLine(testFailingLine);
        if (!string.IsNullOrWhiteSpace(primaryOccurrence.JobUrl))
        {
            AppendInvariantLine(bodyBuilder, $"Logs: {ToMarkdownLink(primaryOccurrence.JobUrl, primaryOccurrence.JobName ?? "Job logs")}");
        }
        if (!string.IsNullOrWhiteSpace(primaryOccurrence.ArtifactUrl))
        {
            AppendInvariantLine(bodyBuilder, $"Artifact: {ToMarkdownLink(primaryOccurrence.ArtifactUrl, primaryOccurrence.ArtifactName)}");
        }
        bodyBuilder.AppendLine();

        bodyBuilder.AppendLine("### Fill in the error message template");
        bodyBuilder.AppendLine();
        bodyBuilder.AppendLine("<!-- Error message template  -->");
        bodyBuilder.AppendLine("## Error Message");
        bodyBuilder.AppendLine();
        bodyBuilder.AppendLine("Fill the error message using [step by step known issues guidance](https://github.com/dotnet/arcade/blob/main/Documentation/Projects/Build%20Analysis/KnownIssueJsonStepByStep.md).");
        bodyBuilder.AppendLine();
        bodyBuilder.AppendLine("<!-- Use ErrorMessage for String.Contains matches. Use ErrorPattern for regex matches (single line/no backtracking). Set BuildRetry to `true` to retry builds with this error. Set ExcludeConsoleLog to `true` to skip helix logs analysis. -->");
        bodyBuilder.AppendLine();
        var jsonBlock = new StringBuilder();
        jsonBlock.AppendLine("{");
        AppendInvariantLine(jsonBlock, $$"""  "ErrorMessage": {{JsonSerializer.Serialize(errorTemplateMessage)}},""");
        jsonBlock.AppendLine("""  "ErrorPattern": "", """.TrimEnd());
        jsonBlock.AppendLine("""  "BuildRetry": false,""");
        jsonBlock.AppendLine("""  "ExcludeConsoleLog": false""");
        jsonBlock.Append('}');
        var jsonContent = jsonBlock.ToString();
        var jsonFence = GetMarkdownFence(jsonContent);
        AppendInvariantLine(bodyBuilder, $"{jsonFence}json");
        bodyBuilder.AppendLine(jsonContent);
        bodyBuilder.AppendLine(jsonFence);
        bodyBuilder.AppendLine();

        AppendErrorDetailsSection(bodyBuilder, errorResult, stackTraceResult);
        AppendStandardOutputSection(bodyBuilder, stdoutResult);

        bodyBuilder.AppendLine(metadataMarker);

        return bodyBuilder.ToString();
    }

    private static string CreateKnownIssueErrorMessage(string? rawErrorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawErrorMessage))
        {
            return string.Empty;
        }

        foreach (var line in rawErrorMessage.Split('\n'))
        {
            var trimmedLine = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedLine))
            {
                return trimmedLine;
            }
        }

        return string.Empty;
    }

    private static void AppendErrorDetailsSection(
        StringBuilder builder,
        TruncationResult errorResult,
        TruncationResult stackTraceResult)
    {
        builder.AppendLine("### Error details");
        builder.AppendLine();

        var notes = new[] { errorResult.Note, stackTraceResult.Note }
            .Where(static note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var note in notes)
        {
            AppendInvariantLine(builder, $"_{note}_");
        }

        if (notes.Length > 0)
        {
            builder.AppendLine();
        }

        var detailsBuilder = new StringBuilder();
        detailsBuilder.Append("Error Message: ");
        detailsBuilder.AppendLine(string.IsNullOrWhiteSpace(errorResult.Content) ? "n/a" : errorResult.Content.TrimEnd());
        detailsBuilder.AppendLine("Stack Trace:");
        detailsBuilder.AppendLine(string.IsNullOrWhiteSpace(stackTraceResult.Content) ? "n/a" : stackTraceResult.Content.TrimEnd());

        var detailsContent = detailsBuilder.ToString().TrimEnd();
        var lineCount = detailsContent.AsSpan().Count('\n') + 1;
        var fence = GetMarkdownFence(detailsContent);
        var collapsible = lineCount > ErrorDetailsCollapsibleLineThreshold;

        if (collapsible)
        {
            builder.AppendLine("<details>");
            AppendInvariantLine(builder, $"<summary>Error details ({lineCount} lines)</summary>");
            builder.AppendLine();
        }

        AppendInvariantLine(builder, $"{fence}yml");
        builder.AppendLine(detailsContent);
        builder.AppendLine(fence);
        builder.AppendLine();

        if (collapsible)
        {
            builder.AppendLine("</details>");
            builder.AppendLine();
        }
    }

    private static void AppendStandardOutputSection(StringBuilder builder, TruncationResult stdoutResult)
    {
        builder.AppendLine("<details>");
        builder.AppendLine("<summary>Standard Output</summary>");
        builder.AppendLine();

        if (string.IsNullOrEmpty(stdoutResult.Content))
        {
            builder.AppendLine("_No content captured._");
            builder.AppendLine();
            builder.AppendLine("</details>");
            builder.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(stdoutResult.Note))
        {
            AppendInvariantLine(builder, $"_{stdoutResult.Note}_");
            builder.AppendLine();
        }

        var fence = GetMarkdownFence(stdoutResult.Content);
        AppendInvariantLine(builder, $"{fence}yml");
        builder.AppendLine(stdoutResult.Content);
        builder.AppendLine(fence);
        builder.AppendLine();
        builder.AppendLine("</details>");
        builder.AppendLine();
    }

    private static void ShrinkSectionUntilWithinLimit(ref TruncationResult section, int minimumLength, Func<string> renderBody)
    {
        var body = renderBody();
        while (body.Length > IssueBodyMaxLength && section.Content.Length > minimumLength)
        {
            var excess = body.Length - IssueBodyMaxLength;
            var nextLength = Math.Max(minimumLength, section.Content.Length - excess - 256);
            if (nextLength >= section.Content.Length)
            {
                break;
            }

            section = TruncateContent(section.Content, nextLength, TruncationPreference.Middle) with
            {
                Note = IssueSizeTruncationNote
            };
            body = renderBody();
        }
    }

    private static string SnipMiddle(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content;
        }

        const string marker = "\n\n... [snipped middle content] ...\n\n";
        if (maxChars <= marker.Length)
        {
            return content[..maxChars];
        }

        var remaining = maxChars - marker.Length;
        var prefixLength = remaining / 2;
        var suffixLength = remaining - prefixLength;

        return string.Concat(
            content.AsSpan(0, prefixLength),
            marker,
            content.AsSpan(content.Length - suffixLength));
    }

    private static void AppendInvariantLine(StringBuilder builder, FormattableString value)
    {
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
    }

    private static string EscapeMarkdownInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Escape characters that have special meaning in GitHub-flavoured Markdown.
        // We deliberately leave # and > alone because they only trigger at line-start
        // and the values we escape are always mid-line.
        return Regex.Replace(text, @"([\\`*_\[\]\(\)~])", @"\$1");
    }

    private static string EscapeBacktickContent(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Replace backticks so they cannot break out of an inline code span.
        return text.Replace("`", "'");
    }

    private static string ToMarkdownLink(string? url, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return text ?? "n/a";
        }

        var displayText = string.IsNullOrWhiteSpace(text) ? url : text;
        // Escape brackets in display text and parentheses in URL to prevent injection.
        var safeDisplay = EscapeMarkdownInline(displayText);
        var safeUrl = url.Replace("(", "%28").Replace(")", "%29");
        return $"[{safeDisplay}]({safeUrl})";
    }

    private static string GetMarkdownFence(string content)
    {
        var maxBackticks = 2;
        foreach (Match match in Regex.Matches(content, @"`+"))
        {
            if (match.Length > maxBackticks)
            {
                maxBackticks = match.Length;
            }
        }

        return new string('`', maxBackticks + 1);
    }

    private sealed record MatchGroupKey(
        string CanonicalName,
        string DisplayName);

    private sealed record MatchGroup(
        MatchGroupKey Key,
        IReadOnlyList<FailedTestOccurrence> Occurrences);
}
