// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>
/// Collects a selected GitHub Actions run's metadata, failed jobs, logs, annotations, TRX/Mocha artifacts,
/// pull-request or main-branch context, and prior cause records into the local evidence directory.
/// This stage fetches evidence; it does not diagnose failures or publish issues.
/// </summary>
internal sealed partial class WorkflowRunEvidenceCollector
{
    internal const string RunnerLostAnnotation = "The hosted runner lost communication with the server.";
    private readonly Dictionary<string, string> _runnerFailures = new(StringComparer.Ordinal);
    private readonly string _workingDirectory;
    private readonly string _repository;
    private readonly AnalysisScratchWorkspace _scratch;
    private readonly InvocationCapture _capture;
    private readonly IGitHubWorkflowConventions _workflow;
    private readonly IReadOnlyDictionary<string, string>? _commandEnvironment;
    private readonly GitHubApiClient _githubApi;
    private AnalyzerCommandExecutor _command;
    private readonly Dictionary<string, JsonNode?> _documents;
    private readonly Dictionary<string, CollectedAttachment> _attachments;
    private int _started;
    private AnalysisProgress _progress = new();
    private IReadOnlyList<SelectedTestArtifact> _selectedArtifacts = [];
    private EvidenceState _testState = EvidenceState.Unavailable;
    private CollectedPrMetadata? _prMetadata;
    private CollectedMainContext? _mainContext;

    private void CaptureDiagnostic(string name, JsonNode? value) => _documents["diagnostics/" + name] = value;
    private void CaptureDiagnostic<T>(string name, T value) => CaptureDiagnostic(name, ToNode(value));

    /// <param name="workingDirectory">Repository checkout used for subprocesses and prior-history checkout.</param>
    /// <param name="repository">GitHub repository in owner/name form.</param>
    /// <param name="scratch">Invocation-owned checkout and test-result workspace.</param>
    /// <param name="capture">Caller-owned captures retained even when collection fails.</param>
    /// <param name="workflow">Trusted conventions for the workflow being collected.</param>
    /// <param name="commandEnvironment">Subprocess overrides; null inherits the current environment.</param>
    public WorkflowRunEvidenceCollector(
        string workingDirectory,
        string repository,
        AnalysisScratchWorkspace scratch,
        InvocationCapture capture,
        IGitHubWorkflowConventions workflow,
        IReadOnlyDictionary<string, string>? commandEnvironment,
        GitHubApiClient github)
    {
        if (!GitHubRepositoryNameRegex().IsMatch(repository))
        {
            throw new ValidationException("Invalid analysis repository");
        }

        _workingDirectory = workingDirectory;
        _repository = repository;
        _scratch = scratch;
        _capture = capture;
        _documents = capture.Documents;
        _attachments = capture.Attachments;
        _workflow = workflow;
        _commandEnvironment = commandEnvironment;
        _githubApi = github;
        _command = new AnalyzerCommandExecutor(workingDirectory, commandEnvironment);
    }

    private static bool IsFailedOrInterrupted(JsonNode? conclusion) => Text(conclusion) is "failure" or "cancelled" or "timed_out";

    /// <summary>Collects the selected workflow run's CI failure evidence.</summary>
    /// <param name="request">Repository, run ID, and optional pinned attempt to collect.</param>
    /// <param name="progress">Known identity, work status, and problems retained if collection fails.</param>
    /// <param name="ct">Cancellation token for acquisition and parsing.</param>
    /// <remarks>
    /// The caller supplies inputs and persists the summary. This collector does not read workflow
    /// environment variables or write GitHub Actions step outputs.
    /// </remarks>
    public async Task<EvidenceCollectionResult> CollectAsync(EvidenceCollectionRequest request, AnalysisProgress progress, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, value: 1) != 0)
        {
            throw new InvalidOperationException("A workflow evidence collector can only be used once.");
        }
        var runId = request.RunId;
        var requestedAttempt = request.Attempt;
        _progress = progress;
        progress.Diagnostics.StartCurrentStage();
        ct.ThrowIfCancellationRequested();
        _command = new AnalyzerCommandExecutor(_workingDirectory, _commandEnvironment, ct);

        // Resolve and verify the requested run and attempt before capturing their identity.
        var gitHub = new AnalysisGitHubClient(_githubApi, ct);
        var runIdText = runId.ToString(CultureInfo.InvariantCulture);
        progress.Diagnostics.WriteProgress($"Requested source run: https://github.com/{_repository}/actions/runs/{runIdText}" +
            (requestedAttempt is null ? " (latest attempt)" : $" (attempt {requestedAttempt.Value.ToString(CultureInfo.InvariantCulture)})"));
        var runMetadata = await gitHub.GetAsync($"repos/{_repository}/actions/runs/{runIdText}" +
            (requestedAttempt is null ? "" : $"/attempts/{requestedAttempt.Value.ToString(CultureInfo.InvariantCulture)}"));
        CaptureDiagnostic("run.json", runMetadata);
        if (!(runMetadata is JsonObject && GitHubJson.ReadRequiredId(Field(runMetadata, "id")) == runId))
        {
            throw new ValidationException("Trusted run metadata does not match the requested run");
        }

        var run = (JsonObject)runMetadata!;
        progress.RunId = runId;

        var runAttempt = run["run_attempt"]?.GetValue<int>() ?? throw new ValidationException("Trusted run metadata is missing its attempt");
        if (runAttempt <= 0)
        {
            throw new ValidationException("Trusted run metadata must contain a positive attempt.");
        }
        if (requestedAttempt is not null && runAttempt != requestedAttempt)
        {
            throw new ValidationException("Trusted run metadata does not match the requested attempt");
        }

        progress.RunAttempt = runAttempt;

        // Admit supported CI runs; success or an unsupported scope is legitimate no-work.
        if (!(Text(run["path"]) == _workflow.WorkflowPath))
        {
            throw new ValidationException($"Run {runId} belongs to workflow '{Display(Text(run["path"]) ?? "")}', not '{Display(_workflow.WorkflowPath)}'");
        }
        RunScope? runScope = Text(run["event"]) switch
        {
            "push" when Text(run["head_branch"]) == "main" => RunScope.Main,
            "pull_request" or "pull_request_target" => RunScope.PullRequest,
            _ => null,
        };
        if (runScope is null || Text(run["conclusion"]) == "success")
        {
            progress.Diagnostics.Record(
                "run-admission",
                runScope is null ? "unsupported-scope" : "successful-run",
                new { workflow = _workflow.WorkflowPath });
            Console.WriteLine("::notice::Run succeeded or has an unsupported scope. Skipping analysis.");
            progress.HasRelevantFailures = false;
            return new(Run: null, HasRelevantFailures: false);
        }

        // Associate the subject PR and select actionable failures, excluding aggregation wrappers.
        progress.HeadSha = Text(run["head_sha"]);
        progress.RunUrl = Text(run["html_url"]);
        progress.RunScope = runScope;
        var subjectPrNumber = runScope == RunScope.PullRequest ? await FindAssociatedPullRequestNumberAsync(gitHub, run) : null;
        progress.SubjectPrNumber = subjectPrNumber;

        progress.Stage = "jobs";
        var allJobs = await gitHub.GetRowsAsync($"repos/{_repository}/actions/runs/{runIdText}/attempts/{runAttempt.ToString(CultureInfo.InvariantCulture)}/jobs", "jobs");
        CaptureDiagnostic("all-jobs.json", allJobs);
        var failedJobs = new JsonArray(allJobs.OfType<JsonObject>().Where(job =>
                IsFailedOrInterrupted(job["conclusion"]) && !_workflow.IsAggregationJob(job))
            .Select(job => job.DeepClone()).ToArray());
        CaptureDiagnostic("failed-jobs.json", failedJobs);
        progress.Diagnostics.Record(
            "jobs",
            failedJobs.Count == 0 ? "no-actionable-jobs" : "actionable",
            new
            {
                total = allJobs.Count,
                actionable = failedJobs.Count,
                failed_or_interrupted = allJobs.Count(job => IsFailedOrInterrupted(Field(job, "conclusion"))),
                excluded = allJobs.Count - failedJobs.Count
            });
        if (failedJobs.Count == 0)
        {
            progress.HasRelevantFailures = false;
            return new(Run: null, HasRelevantFailures: false);
        }

        progress.HasRelevantFailures = true;
        ct.ThrowIfCancellationRequested();
        var jobs = CollectionProjection.ProjectJobs(failedJobs);

        // Enrich diagnosis with branch/PR context, focused logs, annotations, and retry patterns.
        progress.Stage = "context";
        if (runScope == RunScope.Main)
        {
            await CollectMainBranchComparisonContextAsync(gitHub, run, runId);
        }
        await CollectFailedJobLogsAndAnnotationsAsync(gitHub, failedJobs);
        jobs = jobs.Select(job => job with
        {
            RunnerFailure = _runnerFailures.GetValueOrDefault(job.Id.ToString(CultureInfo.InvariantCulture)),
        }).ToImmutableArray();
        if (subjectPrNumber is { } pullRequestNumber)
        {
            await CollectPullRequestContextAsync(gitHub, pullRequestNumber);
        }

        var retryPatternsPath = Path.Combine(_workingDirectory, "eng", "test-retry-patterns.json");
        if (File.Exists(retryPatternsPath))
        {
            var attachment = new CollectedAttachment((await File.ReadAllBytesAsync(retryPatternsPath, ct)).ToImmutableArray());
            try
            {
                CaptureDiagnostic("retry-patterns.json", Parse(attachment.Text));
            }
            catch (JsonException error)
            {
                progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "retry-patterns", error.Message);
                Console.WriteLine($"::warning::Unable to parse retry patterns: {Display(DiagnosticTextSanitizer.Sanitize(error.Message, limit: 500))}");
                _attachments["diagnostics/retry-patterns.json"] = attachment;
            }
        }

        // Keep history acquisition separate: a history failure is fatal, not degraded test evidence.
        progress.Stage = "history";
        await CopyPriorCauseHistoryAsync();
        ct.ThrowIfCancellationRequested();

        // Download and parse bounded test artifacts; supported test-evidence failures may degrade diagnosis.
        progress.Stage = "test-evidence";
        await DownloadAndExtractTestResultArtifactsAsync(gitHub, run, failedJobs, runId, runAttempt);
        ImmutableArray<CollectedTestFailure> failures = [];
        if (_selectedArtifacts.Count > 0)
        {
            const int maximumMochaDiagnosticCharacters = 65536;
            var mochaDiagnostics = new Dictionary<string, (StringBuilder Text, int Characters, int Omitted)>(StringComparer.Ordinal);
            try
            {
                var raw = TestFailureReader.ReadAvailable(
                    _scratch.Results,
                    jobs,
                    _selectedArtifacts,
                    message =>
                    {
                        ReportArtifactProblem(AnalysisProblemCategory.TestEvidence, "parsing", message);
                        _testState = EvidenceState.Unavailable;
                    },
                    (artifact, diagnostic) =>
                    {
                        if (!mochaDiagnostics.TryGetValue(artifact.JobName, out var log))
                        {
                            log = (new StringBuilder(), 0, 0);
                        }
                        // Keep complete, independently redacted entries. After filling the buffer,
                        // count omissions without rescanning old text or sanitizing discarded entries.
                        if (log.Omitted > 0 || log.Characters == maximumMochaDiagnosticCharacters)
                        {
                            log.Omitted++;
                        }
                        else
                        {
                            var entry = "Mocha hook/harness diagnostic: not a completed test failure.\n" +
                                DiagnosticTextSanitizer.Sanitize(diagnostic, limit: 2000, multiline: true) + "\n";
                            var characters = entry.EnumerateRunes().Count();
                            if (log.Characters + characters > maximumMochaDiagnosticCharacters)
                            {
                                log.Omitted++;
                            }
                            else
                            {
                                log.Text.Append(entry);
                                log.Characters += characters;
                            }
                        }
                        mochaDiagnostics[artifact.JobName] = log;
                    });
                var conflicts = raw.Where(test => !string.IsNullOrEmpty(test.Source.ExecutionId))
                    .GroupBy(test => (test.JobId, test.Source.ExecutionId))
                    .Where(group => group.Select(test => (test.Name, test.Diagnostics)).Distinct().Skip(1).Any()).ToArray();
                foreach (var conflict in conflicts)
                {
                    var artifact = _selectedArtifacts.Single(selection =>
                        GitHubJson.ReadRequiredId(selection.Artifact.Id) == conflict.Last().Source.ArtifactId);
                    ReportArtifactProblem(
                        AnalysisProblemCategory.TestEvidence,
                        "parsing",
                        $"Conflicting copied executions were excluded; job={conflict.Key.JobId.ToString(CultureInfo.InvariantCulture)} " +
                        $"execution='{Display(DiagnosticTextSanitizer.Sanitize(conflict.Key.ExecutionId ?? "", limit: 80))}' " +
                        $"artifacts={string.Join(',', conflict.Select(test => test.Source.ArtifactId.ToString(CultureInfo.InvariantCulture)).Distinct())}; " +
                        TestResultArtifacts.Describe(artifact.Expectation));
                    _testState = EvidenceState.Unavailable;
                }
                var conflictingExecutions = conflicts.Select(group => group.Key).ToHashSet();
                failures = TestFailureReader.ConsolidateFailures(raw.Where(test =>
                    !conflictingExecutions.Contains((test.JobId, test.Source.ExecutionId))).ToImmutableArray());
            }
            catch (Exception ex) when (ex is ValidationException or JsonException or IOException or System.Xml.XmlException)
            {
                ReportArtifactProblem(AnalysisProblemCategory.TestEvidence, "parsing", ex.Message);
                _testState = EvidenceState.Unavailable;
            }
            finally
            {
                foreach (var (jobName, log) in mochaDiagnostics)
                {
                    var job = jobs.Single(job => job.Name == jobName);
                    if (log.Omitted > 0)
                    {
                        log.Text.Append(CultureInfo.InvariantCulture,
                            $"\n[Omitted {log.Omitted} further Mocha hook/harness diagnostic entries after reaching the text limit.]\n");
                    }
                    var name = $"diagnostics/job-{job.Id.ToString(CultureInfo.InvariantCulture)}-mocha.log";
                    _attachments[name] = CollectedAttachment.FromText(log.Text.ToString());
                    _progress.Diagnostics.Record("mocha-non-test-failure", log.Omitted > 0 ? "truncated" : "retained",
                        new { job = job.Id, omitted_entries = log.Omitted });
                }
            }
        }

        // Freeze captured facts and attachments; the command boundary owns all output writes.
        ct.ThrowIfCancellationRequested();
        progress.Stage = "projection";
        var prior = PriorCauseCatalogue.BuildIndex(_attachments.ToImmutableDictionary(), failures);
        var evidence = CollectionProjection.CreateBundle(request.Repository, progress, run, jobs, failures, _testState, prior, _prMetadata, _mainContext);
        progress.Diagnostics.Record("test-evidence", EnumText(_testState), new { failures = failures.Length, acquired_artifacts = _selectedArtifacts.Count });
        _capture.Bundle = evidence;
        AnalysisValidator.ValidateBundle(evidence, request.Repository);
        var captured = _documents.ToImmutableDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));
        return new(new CollectedRun(evidence, _attachments.ToImmutableDictionary(), prior) { JsonContext = captured }, HasRelevantFailures: true);
    }

    private async Task CopyPriorCauseHistoryAsync()
    {
        var history = new CauseHistoryStore(_command, _repository, _scratch.HistoryCheckout, _progress.Diagnostics);
        if (await history.OpenForReadAsync())
        {
            _capture.HistoryCommit = await history.GetHeadCommitAsync();
            foreach (var causePath in history.EnumerateCauseFiles())
            {
                _attachments["history/causes/" + Path.GetFileName(causePath)] =
                    new((await File.ReadAllBytesAsync(causePath)).ToImmutableArray());
            }
        }
    }

    private async Task<long?> FindAssociatedPullRequestNumberAsync(AnalysisGitHubClient gitHub, JsonObject run)
    {
        static (long? Number, bool Ambiguous) SelectSinglePullRequestNumber(IEnumerable<JsonNode?> candidates)
        {
            var numbers = candidates.OfType<JsonValue>()
                .Select(GitHubJson.ReadRequiredId).Distinct().ToArray();
            return (numbers.Length == 1 ? numbers[0] : null, numbers.Length > 1);
        }
        var (subjectPrNumber, ambiguous) = SelectSinglePullRequestNumber((run["pull_requests"] as JsonArray ?? []).Where(pullRequest =>
            Text(Field(Field(Field(pullRequest, "base"), "repo"), "url")) == $"https://api.github.com/repos/{_repository}")
            .Select(pullRequest => Field(pullRequest, "number")));
        var method = "run-metadata";
        var mismatchedHead = false;
        var headSha = Text(run["head_sha"]) ?? "";
        if (subjectPrNumber is null && !ambiguous && headSha.Length > 0)
        {
            method = "commit";
            var associatedPullRequests = await gitHub.GetRowsAsync($"repos/{_repository}/commits/{headSha}/pulls?per_page=100");
            (subjectPrNumber, ambiguous) = SelectSinglePullRequestNumber(associatedPullRequests.Where(pullRequest => Text(Field(Field(Field(pullRequest, "base"), "repo"), "full_name")) == _repository)
                .Select(pullRequest => Field(pullRequest, "number")));
        }
        var headOwner = Text(Field(Field(run["head_repository"], "owner"), "login")) ?? "";
        var headBranch = Text(run["head_branch"]) ?? "";
        if (subjectPrNumber is null && !ambiguous && headSha.Length > 0 && headOwner.Length > 0 && headBranch.Length > 0)
        {
            method = "head-branch";
            var associatedPullRequests = await gitHub.GetRowsAsync(
                $"repos/{_repository}/pulls?state=all&per_page=100&head={Uri.EscapeDataString($"{headOwner}:{headBranch}")}");
            mismatchedHead = associatedPullRequests.Count > 0 && !associatedPullRequests.Any(pullRequest => Text(Field(Field(pullRequest, "head"), "sha")) == headSha);
            (subjectPrNumber, ambiguous) = SelectSinglePullRequestNumber(associatedPullRequests.Where(pullRequest => Text(Field(Field(pullRequest, "head"), "sha")) == headSha).Select(pullRequest => Field(pullRequest, "number")));
        }
        if (ambiguous)
        {
            _progress.Diagnostics.Record("subject-pr", "ambiguous", new { method });
            Console.WriteLine("::warning::Multiple associated PRs found. Analysis will proceed without subject PR context.");
            return null;
        }

        _progress.Diagnostics.Record(
            "subject-pr",
            subjectPrNumber is not null ? "resolved" : mismatchedHead ? "head-sha-mismatch" : "no-match",
            new { method, number = subjectPrNumber?.ToString(CultureInfo.InvariantCulture) });
        return subjectPrNumber;
    }

    private async Task CollectMainBranchComparisonContextAsync(AnalysisGitHubClient gitHub, JsonObject run, long runId)
    {
        var mainHistory = new BranchHistoryReader(gitHub, _repository);
        var headSha = Text(run["head_sha"]) ?? "";
        JsonObject triggeringMergePr = [];
        try
        {
            var mergedPullRequests = mainHistory.FindMergedPullRequests(await gitHub.GetRowsAsync($"repos/{_repository}/commits/{headSha}/pulls?per_page=100"));
            if (mergedPullRequests.Length == 1)
            {
                triggeringMergePr = mergedPullRequests[0];
            }
        }
        catch (Exception ex) when (ex is ValidationException or JsonException)
        {
            _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "triggering-merge", ex.Message);
            Console.WriteLine($"::warning::Unable to associate the triggering merge: {Display(ex.Message)}");
        }
        CaptureDiagnostic("triggering-merge-pr.json", triggeringMergePr);
        JsonObject lastSuccessfulRun = [];
        try
        {
            lastSuccessfulRun = await mainHistory.FindLastSuccessfulRunAsync(
                GetValueText(run["workflow_id"]),
                Text(run["created_at"]) ?? "",
                runId);
        }
        catch (Exception ex) when (ex is ValidationException or JsonException or FormatException)
        {
            _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "main-history", ex.Message);
            Console.WriteLine($"::warning::Unable to find the last successful main run. Continuing without a candidate merge range: {Display(ex.Message)}");
        }
        CaptureDiagnostic("last-successful-main-run.json", lastSuccessfulRun);
        var (candidateMerges, historyState) = await mainHistory.FindCandidateMergesAsync(Text(lastSuccessfulRun["head_sha"]) ?? "", headSha);
        _mainContext = CollectionProjection.ProjectMainContext(headSha, lastSuccessfulRun, triggeringMergePr, candidateMerges, historyState);
        CaptureDiagnostic("candidate-merges.json", candidateMerges);
        CaptureDiagnostic("candidate-merge-history-status.json", new CandidateHistoryStatus { State = historyState });
    }

    private async Task CollectFailedJobLogsAndAnnotationsAsync(AnalysisGitHubClient gitHub, JsonArray failedJobs)
    {
        foreach (var job in failedJobs)
        {
            var jobId = GetValueText(job!["id"]);
            GitHubTextDownload logResult;
            try
            {
                logResult = await gitHub.DownloadTextAsync($"repos/{_repository}/actions/jobs/{jobId}/logs", maximumBytes: 10485760);
            }
            catch (ValidationException error)
            {
                logResult = new GitHubTextDownload("", error.Message);
            }
            var logText = logResult.Text;
            if (logResult.Error is not null)
            {
                var problem = $"Unable to fetch complete logs for job {jobId}: " +
                    DiagnosticTextSanitizer.Sanitize(logResult.Error, limit: 300, multiline: false);
                _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "logs", problem);
                Console.WriteLine($"::warning::{Display(problem)}");
            }
            var safeLog = DiagnosticTextSanitizer.Sanitize(logText, limit: 10485760, multiline: true);
            var focused = FocusLogs(safeLog, out var sourceLines, out var retainedLines);
            _progress.Diagnostics.Record(
                "job-log",
                logResult.Error is null ? "acquired" : "partial",
                new
                {
                    job_id = jobId,
                    complete = logResult.Error is null,
                    source_lines = sourceLines,
                    retained_lines = retainedLines,
                    truncated = retainedLines < sourceLines || logText.EnumerateRunes().Count() > 10485760,
                    sanitized = true,
                    focused = true
                });
            if (logResult.Error is not null)
            {
                // Acquisition status is not a source-log line: append it after focusing so
                // the excerpt budget cannot hide a partial or unavailable download.
                focused += $"\nFailed to fetch complete logs for job {jobId}.\n" +
                    DiagnosticTextSanitizer.Sanitize(logResult.Error, limit: 500, multiline: true);
            }
            _attachments[$"diagnostics/job-{jobId}.log"] = CollectedAttachment.FromText(focused);
            JsonArray annotations = [];
            // GitHub supplies URLs such as https://api.github.com/repos/owner/repo/check-runs/88.
            var checkRunUrl = Text(job["check_run_url"]) ?? "";
            if (long.TryParse(
                checkRunUrl.AsSpan(checkRunUrl.LastIndexOf('/') + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var checkRunId) && checkRunId > 0)
            {
                try
                {
                    annotations = await gitHub.GetRowsAsync($"repos/{_repository}/check-runs/{checkRunId.ToString(CultureInfo.InvariantCulture)}/annotations");
                }
                catch (Exception ex) when (ex is ValidationException or JsonException)
                {
                    _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "annotations", ex.Message);
                    Console.WriteLine($"::warning::Unable to load job annotations: {Display(ex.Message)}");
                }
            }
            CaptureDiagnostic($"annotations-{jobId}.json", annotations);
            // Workflow commands can emit arbitrary annotation messages. Require an
            // interrupted step as independent evidence before exempting missing results.
            if ((job["steps"] as JsonArray ?? []).Any(step => Text(Field(step, "status")) == "in_progress") &&
                annotations.OfType<JsonObject>().Any(annotation =>
                Text(annotation["message"])?.StartsWith(RunnerLostAnnotation, StringComparison.Ordinal) == true))
            {
                _runnerFailures[jobId] = RunnerLostAnnotation;
            }
        }
    }

    internal static string FocusLogs(string log) => FocusLogs(log, out _, out _);

    private static string FocusLogs(string log, out int sourceLines, out int retainedLines)
    {
        var lines = log.Split('\n');
        if (lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }
        sourceLines = lines.Length;
        var matches = lines.Select(line => !PassingTestRegex().IsMatch(line) && LogFailureRegex().IsMatch(line)).ToArray();
        var selected = new SortedSet<int>();
        var anchors = Enumerable.Range(start: 0, lines.Length).Where(index => matches[index])
            .OrderByDescending(index => ExplicitFailureRegex().IsMatch(lines[index])).ThenBy(index => index).ToArray();
        // Reserve actual failure lines before context or generic keyword matches can
        // exhaust the excerpt. Render in source order only after selection.
        foreach (var group in anchors.GroupBy(index => ExplicitFailureRegex().IsMatch(lines[index])))
        {
            foreach (var index in group)
            {
                TryAdd(index);
            }
            foreach (var index in group)
            {
                for (var offset = -3; offset <= 5; offset++)
                {
                    if (index + offset >= 0 && index + offset < lines.Length)
                    {
                        TryAdd(index + offset);
                    }
                }
            }
        }
        void TryAdd(int index)
        {
            if (!selected.Add(index))
            {
                return;
            }
            var count = selected.Count;
            var previous = -1;
            foreach (var line in selected)
            {
                if (previous >= 0 && line > previous + 1)
                {
                    count++;
                }
                previous = line;
            }
            if (count > 150)
            {
                selected.Remove(index);
            }
        }
        var output = new List<(string Text, bool Source)>();
        var previous = -1;
        foreach (var i in selected)
        {
            if (previous >= 0 && i > previous + 1)
            {
                output.Add(("--", false));
            }
            output.Add(($"{i + 1}{(matches[i] ? ':' : '-')}{lines[i]}", true));
            previous = i;
        }

        if (output.Count == 0)
        {
            retainedLines = Math.Min(200, lines.Length);
            return string.Join('\n', lines.TakeLast(count: 200)) + "\n";
        }
        var kept = output.Take(150).ToArray();
        retainedLines = kept.Count(line => line.Source);
        return string.Join('\n', kept.Select(line => line.Text)) + "\n";
    }

    private async Task CollectPullRequestContextAsync(AnalysisGitHubClient gitHub, long pullRequestNumber)
    {
        var subjectPrNumber = pullRequestNumber.ToString(CultureInfo.InvariantCulture);
        try
        {
            var files = await gitHub.GetRowsAsync($"repos/{_repository}/pulls/{subjectPrNumber}/files");
            CaptureDiagnostic(
                "pr-files.json",
                new JsonArray(files.Select(file => (JsonNode)new JsonObject(
                new[] { "filename", "status", "additions", "deletions", "changes" }.Select(fieldName => KeyValuePair.Create(fieldName, Field(file, fieldName)?.DeepClone())))).ToArray()));
        }
        catch (Exception ex) when (ex is ValidationException or JsonException)
        {
            _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "pr-files", ex.Message);
            Console.WriteLine($"::warning::Unable to load PR files: {Display(ex.Message)}");
            CaptureDiagnostic("pr-files.json", new JsonArray());
        }
        try
        {
            var pullRequest = await gitHub.GetAsync($"repos/{_repository}/pulls/{subjectPrNumber}");
            if (!(pullRequest is JsonObject))
            {
                throw new ValidationException("GitHub returned invalid PR metadata");
            }
            if (GitHubJson.ReadRequiredId(pullRequest!["number"]) != pullRequestNumber)
            {
                throw new ValidationException("GitHub PR metadata does not match the known subject.");
            }
            _prMetadata = CollectionProjection.ProjectPullRequest(pullRequest);
            CaptureDiagnostic(
                "pr-metadata.json",
                new JsonObject
                {
                    ["number"] = pullRequest!["number"]?.DeepClone(),
                    ["title"] = pullRequest["title"]?.DeepClone(),
                    ["state"] = pullRequest["state"]?.DeepClone(),
                    ["locked"] = pullRequest["locked"]?.DeepClone(),
                    ["user"] = Field(pullRequest["user"], "login")?.DeepClone(),
                    ["head_branch"] = Field(pullRequest["head"], "ref")?.DeepClone(),
                    ["base_branch"] = Field(pullRequest["base"], "ref")?.DeepClone(),
                    ["html_url"] = pullRequest["html_url"]?.DeepClone(),
                });
        }
        catch (Exception ex) when (ex is ValidationException or JsonException)
        {
            _progress.RecordProblem(AnalysisProblemCategory.OptionalContext, "pr-metadata", ex.Message);
            Console.WriteLine($"::warning::Unable to load PR metadata: {Display(ex.Message)}");
            CaptureDiagnostic("pr-metadata.json", new JsonObject());
        }
    }

    private async Task DownloadAndExtractTestResultArtifactsAsync(
        AnalysisGitHubClient gitHub,
        JsonObject run,
        JsonArray failedJobs,
        long runId,
        int runAttempt)
    {
        var evidenceState = EvidenceState.Unavailable;
        var zipDirectory = _scratch.Archives;
        try
        {
            var artifacts = await gitHub.GetRowsAsync($"repos/{_repository}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/artifacts", "artifacts");
            CaptureDiagnostic("artifacts.json", artifacts);
            var plans = new List<TestArtifactExpectation>();
            foreach (var job in failedJobs.OfType<JsonObject>())
            {
                foreach (var plan in _workflow.DescribeTestArtifacts(job, runAttempt))
                {
                    if (plan.JobName != Text(job["name"]))
                    {
                        throw new ValidationException("Workflow conventions cannot change a collected test job's identity: " +
                            $"job='{Display(DiagnosticTextSanitizer.Sanitize(Text(job["name"]) ?? "", limit: 80))}'; " +
                            $"returned {TestResultArtifacts.Describe(plan)}");
                    }
                    plans.Add(plan);
                }
            }
            var explainedAbsences = plans.Where(plan => ExplainedRunnerLoss(plan.JobName) &&
                !artifacts.OfType<JsonObject>().Any(artifact => Text(artifact["name"]) == plan.ArtifactName &&
                    (Text(artifact["created_at"]) is not { } created ||
                        string.CompareOrdinal(created, Text(run["run_started_at"]) ?? "") > 0 &&
                        string.CompareOrdinal(created, Text(run["updated_at"]) ?? "") <= 0))).ToHashSet();
            var selectionProblems = new HashSet<TestArtifactExpectation>();
            var selectedTestArtifacts = TestResultArtifacts.SelectAvailable(
                TestResultArtifacts.ReadArtifacts(artifacts),
                plans,
                Text(run["run_started_at"]) ?? "",
                Text(run["updated_at"]) ?? "",
                _progress.Diagnostics,
                (plan, message) =>
                {
                    if (message.StartsWith("test result artifact is missing", StringComparison.Ordinal) &&
                        explainedAbsences.Contains(plan))
                    {
                        _progress.Diagnostics.Record("artifact-selection", "runner-loss", new { job = plan.JobName, artifact = plan.ArtifactName });
                        return;
                    }
                    selectionProblems.Add(plan);
                    ReportArtifactProblem(
                        plan.RequiresResults ? AnalysisProblemCategory.TestEvidence : AnalysisProblemCategory.OptionalContext,
                        "artifacts",
                        $"{message}; {TestResultArtifacts.Describe(plan)}");
                });
            _progress.Diagnostics.Record(
                "artifact-selection",
                "selected",
                new { available = artifacts.Count, planned = plans.Count, selected = selectedTestArtifacts.Count });
            foreach (var plan in plans)
            {
                var selected = selectedTestArtifacts.Where(selection => selection.Expectation == plan).ToArray();
                _progress.Diagnostics.Record(
                    "artifact-plan",
                    selected.Length == 0 ? "missing-or-ineligible" : "matched",
                    new
                    {
                        job = plan.JobName,
                        artifact = plan.ArtifactName,
                        ids = selected.Select(selection => GetValueText(selection.Artifact.Id)).ToArray()
                    });
            }
            var acquiredArtifacts = new List<SelectedTestArtifact>();
            _selectedArtifacts = acquiredArtifacts;
            var selectedArtifacts = TestResultArtifacts.SerializeSelection(selectedTestArtifacts);
            CaptureDiagnostic("selected-test-result-artifacts.json", selectedArtifacts);
            evidenceState = selectedArtifacts.Count == 0 ? EvidenceState.NotApplicable : EvidenceState.Complete;
            foreach (var plan in plans.Where(plan => plan.RequiresResults &&
                !selectedTestArtifacts.Any(artifact => artifact.Expectation == plan) && !explainedAbsences.Contains(plan)))
            {
                evidenceState = EvidenceState.Unavailable;
                if (!selectionProblems.Contains(plan))
                {
                    ReportArtifactProblem(
                    AnalysisProblemCategory.TestEvidence,
                    "artifacts",
                    $"Structured test results are unavailable; {TestResultArtifacts.Describe(plan)}");
                }
            }
            Directory.CreateDirectory(zipDirectory);
            long remainingExpandedBytes = 1073741824;
            long remainingDownloadedBytes = 1073741824;
            foreach (var selection in selectedTestArtifacts)
            {
                var artifactId = GetValueText(selection.Artifact.Id);
                var format = EnumText(selection.Format);
                var zipPath = Path.Combine(zipDirectory, artifactId + ".zip");
                try
                {
                    if (remainingExpandedBytes <= 0 || remainingDownloadedBytes <= 0)
                    {
                        throw new ValidationException("The shared artifact acquisition budget has been exhausted.");
                    }
                    var artifactMetadata = artifacts.First(candidate => ReadNormalizedNumber(Field(candidate, "id")) == ReadNormalizedNumber(selection.Artifact.Id));
                    if (Field(artifactMetadata, "workflow_run") is JsonObject associatedRun)
                    {
                        if (!(ReadNormalizedNumber(associatedRun["id"]) == ReadNormalizedNumber(run["id"]) && Text(associatedRun["head_sha"]) == Text(run["head_sha"])))
                        {
                            throw new ValidationException("Test result artifact does not match the trusted run and SHA");
                        }
                    }
                    try
                    {
                        await gitHub.DownloadFileAsync(
                            $"repos/{_repository}/actions/artifacts/{artifactId}/zip",
                            zipPath,
                            maximumBytes: Math.Min(104857600, remainingDownloadedBytes));
                    }
                    catch (ValidationException error)
                    {
                        throw new ValidationException($"Unable to download test result artifact: {error.Message}", error);
                    }
                    TestResultArchiveExtractor.ExtractTracked(
                        zipPath,
                        Path.Combine(_scratch.Results, artifactId),
                        remainingExpandedBytes,
                        (long)double.Parse(GetValueText(selection.Artifact.SizeInBytes), CultureInfo.InvariantCulture),
                        format,
                        count => remainingExpandedBytes -= count);
                    acquiredArtifacts.Add(selection);
                    _progress.Diagnostics.Record("artifact-acquisition", "extracted", new { id = artifactId, format });
                }
                catch (Exception ex) when (ex is ValidationException or IOException or InvalidDataException)
                {
                    _progress.Diagnostics.Record("artifact-acquisition", "failed", new { id = artifactId, format, reason = ex.Message });
                    ReportArtifactProblem(
                        selection.RequiresResults
                        ? AnalysisProblemCategory.TestEvidence : AnalysisProblemCategory.OptionalContext,
                        "artifacts",
                        $"Unable to extract test results artifact: {DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 200)}; " +
                        $"{TestResultArtifacts.Describe(selection.Expectation)} id={artifactId}");
                    if (selection.RequiresResults)
                    {
                        evidenceState = EvidenceState.Unavailable;
                    }
                }
                finally
                {
                    // Failed optional downloads also consumed the acquisition budget.
                    // The shared transport retains bounded partial files for this accounting.
                    if (File.Exists(zipPath))
                    {
                        remainingDownloadedBytes -= new FileInfo(zipPath).Length;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ValidationException or JsonException or IOException)
        {
            evidenceState = EvidenceState.Unavailable;
            ReportArtifactProblem(
                AnalysisProblemCategory.TestEvidence,
                "artifacts",
                $"Failed to collect test result artifacts: {ex.Message}");
        }
        _testState = evidenceState;

        bool ExplainedRunnerLoss(string jobName)
        {
            var matches = failedJobs.OfType<JsonObject>().Where(job => Text(job["name"]) == jobName).ToArray();
            return matches.Length == 1 && _runnerFailures.ContainsKey(GetValueText(matches[0]["id"]));
        }
    }

    private void ReportArtifactProblem(AnalysisProblemCategory category, string stage, string message)
    {
        var context = $"run=https://github.com/{_repository}/actions/runs/{_progress.RunId} " +
            $"attempt={_progress.RunAttempt} workflow={DiagnosticTextSanitizer.Sanitize(_workflow.WorkflowPath, limit: 80)} " +
            $"stage={stage}: {message}";
        var safeMessage = Display(DiagnosticTextSanitizer.Sanitize(context, limit: 1000));
        _progress.RecordProblem(category, stage, safeMessage);
        Console.WriteLine($"::warning::{safeMessage}");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex GitHubRepositoryNameRegex();

    // Node reports assertions as "AssertionError [ERR_ASSERTION]: web entered FailedToStart"
    // with the useful message several lines before the final Actions exit-code annotation.
    [GeneratedRegex(@"error [A-Z]{2,}[0-9]|##\[error\]|\bAssertionError(?:\s*\[[A-Z0-9_]+\])?:|\bFAILED\b|\bfailed!\b|Build FAILED|ECONNRESET|ECONNREFUSED|ENOTFOUND|Connection reset by peer|Could not resolve host|Operation timed out|The SSL connection could not be established|The requested URL returned error|403 Forbidden|exit code [1-9]|Process completed with exit code",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogFailureRegex();

    // Mocha emits "2026-10-29T01:00:00Z   ✔ handles failed operations".
    // The test name can mention errors without being a failed test result.
    [GeneratedRegex(@"^\s*(?:\d{4}-\d\d-\d\dT\S+\s+)?[\u2713\u2714]\s", RegexOptions.CultureInvariant)]
    private static partial Regex PassingTestRegex();

    [GeneratedRegex(@"##\[error\]|\bAssertionError(?:\s*\[[A-Z0-9_]+\])?:|error [A-Z]{2,}[0-9]|Build FAILED",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitFailureRegex();
}
