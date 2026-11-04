// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Finds successful workflow history and merged-change candidates for a repository branch.</summary>
internal sealed class BranchHistoryReader(AnalysisGitHubClient github, string repository, string branchName = "main")
{
    private readonly string _branchName = !string.IsNullOrWhiteSpace(branchName)
        ? branchName : throw new ValidationException("Branch name must not be empty.");

    public async Task<JsonObject> FindLastSuccessfulRunAsync(string workflowId, string failedTime, long failedId)
    {
        if (!(failedId >= 0))
        {
            throw new ValidationException("Failed run ID must be numeric.");
        }
        var end = DateTimeOffset.ParseExact(
            failedTime,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).ToUnixTimeSeconds();
        long span = 86400;
        while (end > 0)
        {
            var start = Math.Max(0, end - span);
            var found = await FindLastSuccessfulRunInWindowAsync(start, end, workflowId, failedTime, failedId);
            if (found.ContainsKey("id"))
            {
                return found;
            }
            end = start;
            span *= 4;
        }

        return [];
    }

    private async Task<JsonObject> FindLastSuccessfulRunInWindowAsync(long start, long end, string workflowId, string failedTime, long failedId)
    {
        static string Time(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        async Task<JsonNode?> ReadWorkflowRunsPageAsync(int number) => await github.GetAsync(
            $"repos/{repository}/actions/workflows/{workflowId}/runs?branch={Uri.EscapeDataString(_branchName)}" +
            $"&event=push&status=success&per_page=100&page={number}&created={Uri.EscapeDataString($"{Time(start)}..{Time(end)}")}");

        static bool HasValidWorkflowRunRows(JsonNode? page) => page is JsonObject && Field(page, "workflow_runs") is JsonArray rows &&
            rows.All(run => run is JsonObject && GitHubJson.TryReadId(Field(run, "id"), out _) &&
                Text(Field(run, "created_at")) is not null && Text(Field(run, "head_sha")) is { Length: > 0 });

        var first = await ReadWorkflowRunsPageAsync(number: 1);
        if (!(HasValidWorkflowRunRows(first)
              && ReadNormalizedNumber(Field(first, "total_count")) is not null
              && double.TryParse(GetValueText(Field(first, "total_count")), CultureInfo.InvariantCulture, out var total)
              && total >= 0
              && total == Math.Floor(total)))
        {
            throw new ValidationException("GitHub returned invalid workflow-run metadata.");
        }
        total = double.Parse(GetValueText(first!["total_count"]), CultureInfo.InvariantCulture);
        // Filtered Actions searches cap at 1,000 results. Search newer halves first.
        // https://docs.github.com/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow
        if (total >= 1000)
        {
            if (!(end - start > 1))
            {
                throw new ValidationException("A one-second workflow-run window reached GitHub's 1,000-result cap.");
            }
            var midpoint = start + (end - start) / 2;
            var newer = await FindLastSuccessfulRunInWindowAsync(midpoint, end, workflowId, failedTime, failedId);
            return newer.ContainsKey("id") ? newer : await FindLastSuccessfulRunInWindowAsync(start, midpoint, workflowId, failedTime, failedId);
        }
        var rows = ((JsonArray)first!["workflow_runs"]!).ToList();
        var count = rows.Count;
        for (var page = 2; count == 100; page++)
        {
            var next = await ReadWorkflowRunsPageAsync(page);
            if (!HasValidWorkflowRunRows(next))
            {
                throw new ValidationException("GitHub returned invalid workflow-run page metadata.");
            }
            var nextRows = (JsonArray)next!["workflow_runs"]!;
            count = nextRows.Count;
            rows.AddRange(nextRows);
        }
        var unique = rows.DistinctBy(run => ReadNormalizedNumber(run!["id"])).Count();
        if (!(unique >= total))
        {
            throw new ValidationException($"GitHub returned only {unique} of {total} unique workflow runs.");
        }
        // API ranges include both endpoints; apply the half-open window locally.
        var newest = rows.Where(run => string.CompareOrdinal(Text(run!["created_at"]), Time(start)) >= 0 &&
            (string.CompareOrdinal(Text(run!["created_at"]), Time(end)) < 0 ||
                Time(end) == failedTime && Text(run!["created_at"]) == failedTime &&
                GitHubJson.ReadRequiredId(run["id"]) < failedId))
            .OrderBy(run => Text(run!["created_at"]), StringComparer.Ordinal)
            .ThenBy(run => GitHubJson.ReadRequiredId(run!["id"]))
            .LastOrDefault();

        return newest is JsonObject result ? (JsonObject)result.DeepClone() : [];
    }

    public async Task<(ImmutableArray<CandidateMerge> Candidates, CandidateHistoryState State)> FindCandidateMergesAsync(string baseSha, string failedSha)
    {
        var candidates = ImmutableArray.CreateBuilder<CandidateMerge>();
        if (baseSha.Length == 0 || failedSha.Length == 0)
        {
            return (candidates.ToImmutable(), CandidateHistoryState.Unavailable);
        }
        JsonNode? pages;
        try
        {
            pages = await github.GetPageDocumentsAsync($"repos/{repository}/compare/{baseSha}...{failedSha}?per_page=100");
        }
        catch (Exception ex) when (ex is ValidationException or JsonException)
        {
            Console.WriteLine("::warning::Unable to compare the last successful branch commit with the failed commit.");
            return (candidates.ToImmutable(), CandidateHistoryState.Unavailable);
        }
        if (pages is not JsonArray { Count: > 0 } array || Text(Field(array[0], "status")) != "ahead" ||
            !IsPositiveInteger(Field(array[0], "total_commits")) ||
            array.Any(p => p is not JsonObject || Field(p, "commits") is not JsonArray) ||
            array.SelectMany(p => (JsonArray)p!["commits"]!).Any(c =>
                c is not JsonObject || Text(c["sha"]) is not { Length: > 0 } ||
                c["commit"] is not JsonObject || Text(Field(c["commit"], "message")) is null || Text(c["html_url"]) is null))
        {
            Console.WriteLine("::warning::GitHub returned invalid comparison metadata.");
            return (candidates.ToImmutable(), CandidateHistoryState.Unavailable);
        }
        var commits = array.SelectMany(p => (JsonArray)p!["commits"]!).DistinctBy(c => Text(c!["sha"])).ToArray();
        var total = double.Parse(GetValueText(array[0]!["total_commits"]), CultureInfo.InvariantCulture);
        var state = commits.Length == total ? CandidateHistoryState.Available : CandidateHistoryState.Incomplete;
        if (state == CandidateHistoryState.Incomplete)
        {
            Console.WriteLine($"::warning::GitHub returned {commits.Length} of {total} unique commits in the comparison.");
        }
        foreach (var commit in commits)
        {
            var sha = Text(commit!["sha"])!;
            JsonObject[] prs;
            try
            {
                prs = FindMergedPullRequests(await github.GetRowsAsync($"repos/{repository}/commits/{sha}/pulls?per_page=100"));
            }
            catch (Exception ex) when (ex is ValidationException or JsonException)
            {
                Console.WriteLine($"::warning::Unable to associate commit {sha} with a merged pull request.");
                state = CandidateHistoryState.Incomplete;
                continue;
            }
            if (prs.Length != 1)
            {
                Console.WriteLine($"::warning::Commit {sha} does not have exactly one merged pull request association.");
                state = CandidateHistoryState.Incomplete;
                continue;
            }
            var pr = prs[0];
            candidates.Add(new CandidateMerge
            {
                Sha = ToContract<JsonElement?>(commit["sha"]),
                Message = Text(Field(commit["commit"], "message")),
                HtmlUrl = Text(commit["html_url"]),
                PullRequest = new CandidatePullRequest
                {
                    Number = ToContract<JsonElement?>(pr["number"]),
                    Title = Text(pr["title"]),
                    Url = Text(pr["html_url"]),
                    MergedAt = ToContract<JsonElement?>(pr["merged_at"]),
                },
            });
        }

        return (candidates.ToImmutable(), state);
    }

    public JsonObject[] FindMergedPullRequests(JsonArray candidates)
        => candidates.OfType<JsonObject>().Where(pr => Text(Field(Field(pr["base"], "repo"), "full_name")) == repository &&
            Text(Field(pr["base"], "ref")) == _branchName && pr["merged_at"] is not null)
            .DistinctBy(pr => GitHubJson.ReadRequiredId(pr["number"])).ToArray();
}
