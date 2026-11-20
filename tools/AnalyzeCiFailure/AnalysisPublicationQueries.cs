// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Reads fresh cause destinations, PR actionability and existing analysis comments before publication effects.</summary>
internal sealed class AnalysisPublicationQueries(AnalysisGitHubClient github)
{
    internal async Task<(JsonArray Open, JsonArray Closed)> GetCauseIssuesAsync(string repository)
    {
        async Task<JsonArray> State(string state)
        {
            try
            {
                var issues = await github.GetRowsAsync($"repos/{repository}/issues?state={state}&labels=ci-failure-cause&per_page=100");
                return new JsonArray(issues.Where(issue => CiFailureJson.Field(issue, "pull_request") is null)
                    .Select(issue => issue?.DeepClone()).ToArray());
            }
            catch (Exception ex) when (ex is ValidationException or System.Text.Json.JsonException)
            {
                throw new ValidationException($"Failed to load {state} cause issues: {ex.Message}");
            }
        }
        var open = await State("open");
        var closed = await State("closed");

        return (open, closed);
    }

    internal async Task<bool> IsPullRequestOpenAndUnlockedAsync(string repository, string pr)
    {
        JsonNode? response;
        try
        {
            response = await github.GetAsync($"repos/{repository}/pulls/{pr}");
        }
        catch (Exception ex) when (ex is ValidationException or System.Text.Json.JsonException)
        {
            throw new ValidationException($"Unable to determine whether PR #{pr} is actionable: {ex.Message}");
        }
        if (!(response is JsonObject && CiFailureJson.Text(response["state"]) is not null &&
            response["locked"] is JsonValue locked && locked.TryGetValue<bool>(out _)))
        {
            throw new ValidationException($"Unable to determine whether PR #{pr} is actionable");
        }

        return CiFailureJson.Text(response!["state"]) == "open" && CiFailureJson.IsFalse(response["locked"]);
    }

    internal async Task<string> FindAnalysisCommentIdAsync(string repository, string pr)
    {
        JsonArray comments;
        try
        {
            comments = await github.GetRowsAsync($"repos/{repository}/issues/{pr}/comments?per_page=100");
        }
        catch (Exception ex) when (ex is ValidationException or System.Text.Json.JsonException)
        {
            throw new ValidationException($"Failed to list existing analysis comments: {ex.Message}");
        }
        var match = comments.Where(comment => CiFailureJson.Text(CiFailureJson.Field(CiFailureJson.Field(comment, "user"), "login")) == "github-actions[bot]" &&
            (CiFailureJson.Text(CiFailureJson.Field(comment, "body")) ?? "").StartsWith("<!-- analyze-ci-failure -->\n", StringComparison.Ordinal))
            .OrderBy(comment => GitHubJson.ReadRequiredId(CiFailureJson.Field(comment, "id")))
            .FirstOrDefault();

        return match is null ? "" : CiFailureJson.GetValueText(CiFailureJson.Field(match, "id"));
    }
}
