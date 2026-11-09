// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Net;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;
using static AnalyzeCiFailure.ValidationRules;

namespace AnalyzeCiFailure;

/// <summary>
/// Publishes validated causes as GitHub issues, records occurrences and issue links on the cause-history
/// branch, and updates applicable PR comments. Fresh history and issue destinations are checked before writes;
/// an artifact claiming to be "validated" is never a publication authority.
/// </summary>
internal sealed partial class AnalysisPublisher
{
    private readonly string _requestDirectory;
    private readonly string _historyDirectory;
    private readonly string _repository;
    private readonly string _observedAt;
    private readonly string _workingDirectory;
    private AnalyzerCommandExecutor _command;
    private readonly AnalysisDiagnostics _diagnostics;
    private readonly GitHubApiClient _githubApi;
    private AnalysisGitHubClient _github;
    private string? _overflowIssueUrl;

    /// <param name="workingDirectory">Repository checkout used for GitHub and Git subprocesses.</param>
    /// <param name="requestDirectory">Directory used for temporary issue bodies and PR comment requests.</param>
    /// <param name="historyDirectory">Local checkout of the cause-history branch.</param>
    /// <param name="repository">GitHub repository in owner/name form.</param>
    /// <param name="observedAt">Timestamp recorded for new run occurrences.</param>
    /// <param name="diagnostics">Caller-owned decisions and confirmed publication receipts.</param>
    internal AnalysisPublisher(
        string workingDirectory,
        string requestDirectory,
        string historyDirectory,
        string repository,
        string observedAt,
        AnalysisDiagnostics diagnostics,
        GitHubApiClient github)
    {
        if (!GitHubRepositoryNameRegex().IsMatch(repository))
        {
            throw new ValidationException("Invalid publication repository");
        }
        if (!DateTimeOffset.TryParse(
            observedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _))
        {
            throw new ValidationException("Invalid observation timestamp");
        }

        _requestDirectory = requestDirectory;
        _historyDirectory = historyDirectory;
        _repository = repository;
        _observedAt = observedAt;
        _workingDirectory = workingDirectory;
        _command = new AnalyzerCommandExecutor(workingDirectory);
        _diagnostics = diagnostics;
        _githubApi = github;
        _github = new AnalysisGitHubClient(github, CancellationToken.None);
    }

    public async Task PublishAsync(ValidatedAnalysis analysis, CancellationToken cancellationToken)
    {
        _overflowIssueUrl = null;
        cancellationToken.ThrowIfCancellationRequested();
        _command = new AnalyzerCommandExecutor(_workingDirectory, cancellationToken: cancellationToken);
        _github = new AnalysisGitHubClient(_githubApi, cancellationToken);
        Directory.CreateDirectory(_requestDirectory);

        // Deterministic PR failures produce a comment, not recurring cause-history records.
        if (analysis.Report.Verdict == AnalysisVerdict.CodeIssue)
        {
            _diagnostics.Record("publication", "comment-only", new { verdict = analysis.Report.Verdict });
            await PublishCommentAsync(analysis);
            return;
        }
        _diagnostics.Record("publication", "recurring-history", new { verdict = analysis.Report.Verdict, causes = analysis.Causes.Length });

        // Preflight every cause and destination against fresh history before persistent writes.
        var history = new CauseHistoryStore(_command, _repository, _historyDirectory, _diagnostics);
        await history.OpenForPublicationAsync();

        var runId = JsonNode.Parse(analysis.Evidence.Context.RunId!.Value.GetRawText())!;
        var prepared = PrepareCauses(analysis, runId, history);
        if (UsesAggregatePublication(prepared.Count))
        {
            var storedRunPath = history.RunPath(GetValueText(runId));
            var aggregate = await PrepareOverflowAsync(
                analysis,
                prepared,
                File.Exists(storedRunPath) ? Read(storedRunPath) as JsonObject : null);
            await PersistOccurrencesAsync(analysis, analysis.CapturedPublication, runId, prepared, history);
            _overflowIssueUrl = await PublishOverflowAsync(aggregate);
            var runPath = history.RunPath(GetValueText(runId));
            var record = (JsonObject)Read(runPath)!;
            record["overflow_issue_url"] = _overflowIssueUrl;
            Write(runPath, record);
            await history.CommitAndPushAsync(GetValueText(runId), prepared.Select(item => item.Path).Append(runPath));
            await PublishCommentAsync(analysis);
            return;
        }
        var destinations = await ResolveDestinationsAsync(prepared);
        var evidence = analysis.CapturedPublication;

        // Persist occurrences before issue effects so retries can recover from partial publication.
        cancellationToken.ThrowIfCancellationRequested();
        await PersistOccurrencesAsync(analysis, evidence, runId, prepared, history);
        await PublishIssuesAsync(analysis, evidence, runId, prepared, destinations);

        // Save resolved issue links, then update the subject PR's analysis comment.
        await history.CommitAndPushAsync(GetValueText(runId), prepared.Select(p => p.Path));
        await PublishCommentAsync(analysis);
    }

    private List<PreparedCause> PrepareCauses(ValidatedAnalysis analysis, JsonNode runId, CauseHistoryStore store)
    {
        var prepared = new List<PreparedCause>();
        // Check every cause against fresh history before pushing conflicting identities.
        // Resolve every destination before persisting history or mutating any issue.
        foreach (var cause in analysis.Causes)
        {
            var path = store.CausePath(cause.FileName);
            var occurrence = CreateOccurrence(analysis, cause, runId, _observedAt);
            JsonObject? existing = null;
            var replayed = false;
            if (File.Exists(path))
            {
                existing = Read(path) as JsonObject ?? throw new ValidationException($"Stored cause is not readable: {cause.FileName}");
                if (!TryReadEnum<CauseType>(Text(existing["type"]), out var storedType))
                {
                    throw new ValidationException($"Stored cause type is not readable: {cause.FileName}");
                }
                var stored = ToContract<PriorCauseRecord>(existing) ?? throw new ValidationException($"Stored cause is not readable: {cause.FileName}");
                stored.RequireReadableCollections();
                var identity = new StoredCauseIdentity(storedType, stored.TestName) { Id = stored.Id };
                if (!(identity.Id == cause.Id))
                {
                    throw new ValidationException($"Stored cause ID must match its filename: {cause.FileName}");
                }
                RequireUnchangedCauseType(cause, identity);
                replayed = (existing["occurrences"] as JsonArray ?? []).Any(o => ReadNormalizedNumber(Field(o, "run_id")) == ReadNormalizedNumber(runId));
                if (!cause.IsExistingProposal && !replayed)
                {
                    throw new ValidationException($"New cause ID already exists in fresh history: {cause.FileName}");
                }
            }
            else if (cause.IsExistingProposal)
            {
                throw new ValidationException($"Existing cause is missing from fresh history: {cause.FileName}");
            }
            var definition = existing ?? (JsonObject?)ToNode(cause.Definition)
                ?? throw new ValidationException($"New cause is missing its definition: {cause.FileName}");
            var history = MergeCause(definition, existing, occurrence);
            prepared.Add(new PreparedCause(cause, history, path, replayed));
        }

        return prepared;
    }

    private async Task<Dictionary<string, JsonObject?>> ResolveDestinationsAsync(List<PreparedCause> prepared)
    {
        JsonArray? issueInventory = null;
        var destinations = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var (cause, history, _, _) in prepared)
        {
            JsonObject? issue = null;
            var linked = Text(history["issue_url"]);
            if (linked is not null && Regex.IsMatch(linked, $@"^https://github\.com/{Regex.Escape(_repository)}/issues/[1-9][0-9]*\z"))
            {
                _ = TransientJson.ParseId(linked.Split('/')[^1]);
                issue = await _github.GetAsync($"repos/{_repository}/issues/{linked.Split('/')[^1]}") as JsonObject;
                if (!(issue is not null && GetValueText(issue["number"]) == linked.Split('/')[^1] && MatchesIssue(issue, cause)))
                {
                    throw new ValidationException("Linked cause issue detail does not match the cause; review its destination before retrying");
                }
            }
            if (issue is null)
            {
                issueInventory ??= await LoadIssueInventoryAsync();
                issue = issueInventory.OfType<JsonObject>().Where(i => MatchesIssue(i, cause))
                    .OrderBy(i => Text(i["state"]) == "closed").FirstOrDefault();
                if (issue is not null)
                {
                    // Inventory identifies the issue, but only a fresh detail response
                    // can check its current disposition before history or issue writes.
                    var number = GitHubJson.ReadRequiredId(issue["number"]).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    issue = await _github.GetAsync($"repos/{_repository}/issues/{number}") as JsonObject;
                    if (!(issue is not null && GetValueText(issue["number"]) == number && MatchesIssue(issue, cause)))
                    {
                        throw new ValidationException("Cause issue detail lookup returned invalid results");
                    }
                }
            }
            if (!(issue is null || GitHubJson.TryReadId(issue["number"], out _) && Text(issue["state"]) is "open" or "closed"))
            {
                throw new ValidationException("Cause issue detail lookup returned invalid results");
            }
            if (!(issue is null || GetValueText(issue["locked"]) != "true"))
            {
                throw new ValidationException($"Cause {cause.Id} destination is locked; review its destination before retrying");
            }
            if (!(issue is null || Text(issue["state"]) != "closed" || Text(issue["state_reason"]) != "duplicate"))
            {
                throw new ValidationException($"Cause {cause.Id} destination is closed as a duplicate; review its destination before retrying");
            }
            destinations.Add(cause.Id, issue);
        }

        return destinations;
    }

    private async Task PersistOccurrencesAsync(
        ValidatedAnalysis analysis,
        PublicationEvidence evidence,
        JsonNode runId,
        List<PreparedCause> prepared,
        CauseHistoryStore store)
    {
        store.CreateRecordDirectories();
        var runPath = store.RunPath(GetValueText(runId));
        var storedRun = File.Exists(runPath) ? Read(runPath) as JsonObject : null;
        var record = (JsonObject)ToNode(RunSummaryBuilder.Create(analysis.Report, analysis.Evidence, evidence, _observedAt))!;
        if (storedRun?["overflow_issue_url"] is { } overflow)
        {
            record["overflow_issue_url"] = overflow.DeepClone();
        }
        Write(runPath, record);
        foreach (var item in prepared)
        {
            Write(item.Path, item.History);
        }
        await store.CommitAndPushAsync(GetValueText(runId), prepared.Select(p => p.Path).Append(runPath));
    }

    private async Task PublishIssuesAsync(
        ValidatedAnalysis analysis,
        PublicationEvidence evidence,
        JsonNode runId,
        List<PreparedCause> prepared,
        Dictionary<string, JsonObject?> destinations)
    {
        foreach (var (cause, history, path, replayed) in prepared)
        {
            var issue = destinations[cause.Id];
            var (body, title, labels) = BuildIssueUpdate(analysis, evidence, cause, history, issue, runId, replayed);
            if (body is null)
            {
                _diagnostics.Receipt("issue-body", "skipped", new { cause = cause.Id, reason = "unsupported-section-or-budget" });
                Console.WriteLine($"::warning::Cause {cause.Id} has an unsupported managed section or exceeds the publication budget. Skipping body update.");
                if (issue is null)
                {
                    continue;
                }
            }
            var bodyPath = Path.Combine(_requestDirectory, $"{cause.Id}.issue-body.md");
            if (body is not null)
            {
                await File.WriteAllTextAsync(bodyPath, body);
            }
            var main = cause.Type == CauseType.MainRepositoryBreakage;
            if (main)
            {
                await UpsertMainLabelAsync(cause.Id);
            }
            if (issue is not null)
            {
                var number = GetValueText(issue["number"]);
                var request = new JsonObject();
                if (body is not null)
                {
                    request["body"] = body;
                }
                if (main)
                {
                    request["title"] = title;
                }
                if (body is not null || main)
                {
                    await ExecuteEffectAsync(
                        "issue-edit",
                        cause.Id,
                        $"https://github.com/{_repository}/issues/{number}",
                        HttpMethod.Patch,
                        $"repos/{_repository}/issues/{number}",
                        request,
                        HttpStatusCode.OK);
                }
                if (main)
                {
                    // PATCH labels replaces the complete set. The additive endpoint
                    // preserves operator-owned labels when updating a recurring cause.
                    // https://docs.github.com/en/rest/issues/labels#add-labels-to-an-issue
                    await ExecuteEffectAsync(
                        "issue-label-add",
                        cause.Id,
                        $"https://github.com/{_repository}/issues/{number}",
                        HttpMethod.Post,
                        $"repos/{_repository}/issues/{number}/labels",
                        new JsonObject { ["labels"] = new JsonArray(labels.Split(',').Select(label => JsonValue.Create(label)).ToArray()) },
                        HttpStatusCode.OK);
                }
                if (Text(issue["state"]) == "closed")
                {
                    await ExecuteEffectAsync(
                        "issue-reopen",
                        cause.Id,
                        $"https://github.com/{_repository}/issues/{number}",
                        HttpMethod.Patch,
                        $"repos/{_repository}/issues/{number}",
                        new JsonObject { ["state"] = "open" },
                        HttpStatusCode.OK);
                }
                history["issue_url"] = $"https://github.com/{_repository}/issues/{number}";
            }
            else
            {
                var url = await ExecuteEffectAsync(
                    "issue-create",
                    cause.Id,
                    destination: null,
                    HttpMethod.Post,
                    $"repos/{_repository}/issues",
                    new JsonObject
                    {
                        ["title"] = title,
                        ["body"] = body,
                        ["labels"] = new JsonArray(labels.Split(',').Select(label => JsonValue.Create(label)).ToArray()),
                    },
                    HttpStatusCode.Created);
                history["issue_url"] = url;
            }
            Write(path, history);
        }
    }

    private static CauseIssueRenderer.IssueDraft BuildIssueUpdate(
        ValidatedAnalysis analysis,
        PublicationEvidence evidence,
        FailureCause cause,
        JsonObject history,
        JsonObject? issue,
        JsonNode runId,
        bool replayed)
    {
        var occurrence = ((JsonArray)history["occurrences"]!).First(o => ReadNormalizedNumber(Field(o, "run_id")) == ReadNormalizedNumber(runId))!;
        var date = Text(occurrence["observed_at"])!.Split('T')[0];
        var jobs = cause.JobIds.Select(id => analysis.Jobs.First(job => job.Id == id).Name).ToArray();
        var displayJobs = CauseIssueRenderer.JobNames(jobs, "display");
        var tableJobs = CauseIssueRenderer.JobNames(jobs, "table");
        if (replayed)
        {
            // History stores an authenticated aggregate, not a reversible list of
            // job names. Do not infer its members from a later cause or analysis.
            var storedJob = DiagnosticTextSanitizer.Sanitize(Text(occurrence["job"]) ?? "");
            displayJobs = MarkdownFormatting.CodeSpan(storedJob);
            tableJobs = MarkdownFormatting.CodeSpan(storedJob.Replace("|", "\\|", StringComparison.Ordinal));
        }
        var pr = GetValueText(occurrence["pr_number"]);
        var scope = pr != "0" ? $"#{pr}" : analysis.Run.Scope == RunScope.Main ? "main" : "unavailable";
        var row = $"| {date} | [{GetValueText(runId)}]({Text(occurrence["run_url"])}) | {tableJobs} | {scope} |";
        var canonical = CauseIssueRenderer.RenderCanonicalIssue(
            history,
            analysis.Evidence.Context,
            evidence,
            analysis.Run.Url,
            EnumText(analysis.Run.Scope),
            pr,
            displayJobs,
            row);
        var body = canonical.Body!;
        if (issue is not null)
        {
            body = Text(issue["body"]) ?? "";
            try
            {
                if (!CauseIssueRenderer.HasOccurrence(body, GetValueText(runId)))
                {
                    body = CauseIssueRenderer.AppendOccurrence(body, row, ((JsonArray)history["occurrences"]!).Count);
                }
            }
            catch (ValidationException ex)
            {
                Console.WriteLine($"::warning::Cause {cause.Id} has an unsupported occurrence section. Keeping existing body: {Display(ex.Message)}");
                if (cause.Type != CauseType.MainRepositoryBreakage)
                {
                    return canonical with { Body = null };
                }
            }
            if (cause.Type == CauseType.MainRepositoryBreakage)
            {
                try
                {
                    body = CauseIssueRenderer.MigrateMainBody(body, canonical.Body!);
                }
                catch (ValidationException ex)
                {
                    Console.WriteLine($"::warning::Unable to migrate main issue body for {cause.Id}: {Display(ex.Message)}");
                }
            }
        }

        return canonical with { Body = CauseIssueRenderer.FitIssue(body, CauseIssueRenderer.Observations(history)) };
    }

    internal static JsonObject MergeCause(JsonObject definition, JsonObject? existing, JsonObject occurrence)
    {
        var history = (JsonObject)(existing ?? definition).DeepClone();
        history.Remove("job_ids");
        history.Remove("job_names");
        history.Remove("tests");
        // Preserve first-stored cause/run authority. Legacy rows have no attempt:
        // neither attempts nor a new replay/conflict policy are inferred here.
        var occurrences = ((history["occurrences"] as JsonArray ?? []).Append(occurrence))
            .DistinctBy(o => (ReadNormalizedNumber(Field(o, "run_id")), Text(Field(o, "run_id"))))
            .OrderBy(o => Text(Field(o, "observed_at")), StringComparer.Ordinal);
        history["occurrences"] = new JsonArray(occurrences.Select(o => o!.DeepClone()).ToArray());

        return history;
    }

    internal static JsonObject CreateOccurrence(ValidatedAnalysis analysis, FailureCause cause, JsonNode runId, string observedAt)
    {
        var jobNames = cause.JobIds.Select(id => analysis.Jobs.First(j => j.Id == id).Name);
        var occurrence = new CauseOccurrence
        {
            RunId = ToContract<System.Text.Json.JsonElement?>(runId),
            RunUrl = analysis.Run.Url,
            Job = CauseIssueRenderer.JobNames(jobNames, "plain"),
            ObservedAt = observedAt,
            PrNumber = ToContract<System.Text.Json.JsonElement?>(analysis.Run.Scope == RunScope.PullRequest &&
                PullRequestNumberRegex().IsMatch(analysis.Run.PrNumbers)
                ? JsonNode.Parse(analysis.Run.PrNumbers) : JsonValue.Create(value: 0)),
        };
        if (!cause.Tests.IsDefault)
        {
            var collectedTests = analysis.Tests;
            var tests = cause.Tests.Select(observation =>
            {
                var evidence = collectedTests.First(test => test.Name == observation.Name &&
                    AsNormalizedNumber(test.JobId) == observation.JobId &&
                    test.ObservationId == observation.ObservationId);
                // Evidence is rebound by validation. Copy diagnostics and provenance only
                // from the collector, never from the cause's agent-authored references.
                return new OccurrenceTest
                {
                    Name = evidence.DisplayName,
                    JobId = evidence.JobId,
                    Job = evidence.Job,
                    Error = evidence.Error,
                    StackTrace = evidence.StackTrace,
                    StandardOutput = evidence.StandardOutput,
                    StandardError = evidence.StandardError,
                    ResultFile = evidence.ResultFile,
                    ResultFormat = evidence.ResultFormat,
                    ObservationId = evidence.ObservationId,
                    ExecutionId = evidence.ExecutionId,
                };
            }).ToImmutableArray();
            occurrence = occurrence with { Tests = tests };
        }

        return (JsonObject)ToNode(occurrence)!;
    }

    private static bool MatchesIssue(JsonObject issue, FailureCause cause)
    {
        var lines = (Text(issue["body"]) ?? "").ReplaceLineEndings("\n").Split('\n');
        return issue["pull_request"] is null && issue["labels"] is JsonArray labels &&
            labels.Any(l => Text(Field(l, "name")) == "ci-failure-cause") &&
            lines.Length > 1 && lines[0] == $"<!-- ci-failure-cause:{cause.Id} -->" &&
            (lines[1] == $"<!-- ci-failure-cause-type:{EnumText(cause.Type)} -->" ||
                lines.Where(l => l.StartsWith("**Type**: ", StringComparison.Ordinal)).SequenceEqual([$"**Type**: {EnumText(cause.Type)}"]));
    }

    private async Task<JsonArray> LoadIssueInventoryAsync()
    {
        var (open, closed) = await new AnalysisPublicationQueries(_github).GetCauseIssuesAsync(_repository);
        return new JsonArray(open.Concat(closed).Select(issue => issue?.DeepClone()).ToArray());
    }

    private async Task PublishCommentAsync(ValidatedAnalysis analysis)
    {
        if (analysis.Run.Scope != RunScope.PullRequest || !PullRequestNumberRegex().IsMatch(analysis.Run.PrNumbers))
        {
            _diagnostics.Receipt("pr-comment", "skipped", new { reason = "no-subject-pr" });
            return;
        }
        var pr = analysis.Run.PrNumbers;
        var github = _github;
        var queries = new AnalysisPublicationQueries(github);
        string existing;
        try
        {
            if (!await queries.IsPullRequestOpenAndUnlockedAsync(_repository, pr))
            {
                _diagnostics.Receipt("pr-comment", "skipped", new { reason = "not-actionable", pr });
                return;
            }
            existing = await queries.FindAnalysisCommentIdAsync(_repository, pr);
        }
        catch (ValidationException ex)
        {
            _diagnostics.Receipt("pr-comment", "skipped", new { reason = "state-unknown", pr, error = ex.Message });
            Console.WriteLine($"::warning::Comment state is unknown. Skipping comment: {Display(ex.Message)}");
            return;
        }
        var comment = PrCommentRenderer.Render(analysis.Report, analysis.Evidence.Jobs, analysis.Run.Url);
        if (_overflowIssueUrl is not null)
        {
            comment += $"\n[Aggregate CI failure report]({_overflowIssueUrl})\n";
        }
        var file = Path.Combine(_requestDirectory, "analysis-comment.md");
        var request = Path.Combine(_requestDirectory, "analysis-comment-request.json");
        await File.WriteAllTextAsync(file, comment);
        Write(request, new JsonObject { ["body"] = comment });
        try
        {
            if (!await queries.IsPullRequestOpenAndUnlockedAsync(_repository, pr))
            {
                _diagnostics.Receipt("pr-comment", "skipped", new { reason = "no-longer-actionable", pr });
                return;
            }
        }
        catch (ValidationException ex)
        {
            _diagnostics.Receipt("pr-comment", "skipped", new { reason = "state-unknown", pr, error = ex.Message });
            Console.WriteLine($"::warning::PR state is unknown. Skipping comment: {Display(ex.Message)}");
            return;
        }
        if (existing.Length > 0)
        {
            await ExecuteEffectAsync(
                "pr-comment-edit",
                cause: null,
                $"https://github.com/{_repository}/pull/{pr}#issuecomment-{existing}",
                HttpMethod.Patch,
                $"repos/{_repository}/issues/comments/{existing}",
                new JsonObject { ["body"] = comment },
                HttpStatusCode.OK);
        }
        else
        {
            await ExecuteEffectAsync(
                "pr-comment-create",
                cause: null,
                $"https://github.com/{_repository}/pull/{pr}",
                HttpMethod.Post,
                $"repos/{_repository}/issues/{pr}/comments",
                new JsonObject { ["body"] = comment },
                HttpStatusCode.Created);
        }
    }

    private async Task UpsertMainLabelAsync(string cause)
    {
        var endpoint = $"repos/{_repository}/labels/main-ci-break";
        var exists = true;
        try
        {
            _ = await _github.GetAsync(endpoint);
        }
        catch (ValidationException error) when (error.InnerException is GitHubApiException { StatusCode: HttpStatusCode.NotFound })
        {
            exists = false;
        }
        await ExecuteEffectAsync(
            "label-upsert",
            cause,
            destination: null,
            exists ? HttpMethod.Patch : HttpMethod.Post,
            exists ? endpoint : $"repos/{_repository}/labels",
            new JsonObject
            {
                ["name"] = "main-ci-break",
                ["color"] = "b60205",
                ["description"] = "Deterministic repository breakage on the main branch",
            },
            exists ? HttpStatusCode.OK : HttpStatusCode.Created);
    }

    private async Task<string> ExecuteEffectAsync(
        string action,
        string? cause,
        string? destination,
        HttpMethod method,
        string endpoint,
        JsonNode? request,
        HttpStatusCode expectedStatus)
    {
        try
        {
            var response = await _github.SendJsonAsync(method, endpoint, request, expectedStatus);
            var createsComment = action is "pr-comment-create" or "overflow-comment-create";
            var returnsCreatedUrl = action == "issue-create" || createsComment;
            var result = "";
            if (returnsCreatedUrl)
            {
                result = Text(Field(response, "html_url")) ?? "";
                var idField = createsComment ? "id" : "number";
                var validId = GitHubJson.TryReadId(Field(response, idField), out var id);
                var pattern = createsComment
                    ? "^" + Regex.Escape(destination!).Replace("/pull/", "/(?:pull|issues)/", StringComparison.Ordinal) +
                        "#issuecomment-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"\z"
                    : "^" + Regex.Escape($"https://github.com/{_repository}/issues/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}") + @"\z";
                if (!validId || !Regex.IsMatch(result, pattern))
                {
                    throw new ValidationException(createsComment
                        ? "Comment creation did not return a canonical comment URL"
                        : "Issue creation did not return a canonical issue URL");
                }
            }
            _diagnostics.Receipt(action, "confirmed", new { cause, url = returnsCreatedUrl ? result : destination });
            return result;
        }
        catch (Exception error)
        {
            // A failed response or cancellation cannot establish whether the remote accepted
            // the mutation. Receipts describe observations, never authorize retries or effects.
            _diagnostics.Receipt(action, "unknown", new { cause, url = destination, reason = error.Message });
            throw;
        }
    }

    /// <summary>Holds a preflighted cause, its merged history, storage destination and occurrence replay state.</summary>
    private sealed record PreparedCause(FailureCause Cause, JsonObject History, string Path, bool Replayed);

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex GitHubRepositoryNameRegex();

    [GeneratedRegex(@"^[0-9]+\z")]
    private static partial Regex PullRequestNumberRegex();
}
