// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestTools;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Applies analyzer error and metadata policies to the shared GitHub HTTP transport.</summary>
internal sealed class AnalysisGitHubClient(GitHubApiClient githubApi, CancellationToken cancellationToken)
{
    private readonly GitHubApiClient _githubApi = githubApi;

    internal Task<JsonNode?> GetAsync(string endpoint)
        => TranslateAsync(() => _githubApi.GetJsonAsync(endpoint, cancellationToken));

    internal Task<JsonArray> GetPageDocumentsAsync(string endpoint)
        => TranslateAsync(() => _githubApi.GetPagesAsync(endpoint, cancellationToken));

    internal async Task<JsonArray> GetRowsAsync(string endpoint, string? property = null)
    {
        var response = await GetPageDocumentsAsync(endpoint);
        var result = new JsonArray();
        var pageNumber = 0;
        foreach (var page in (JsonArray)response!)
        {
            pageNumber++;
            var rows = property is null ? page : CiFailureJson.Field(page, property);
            if (!(rows is JsonArray))
            {
                throw new ValidationException("GitHub returned invalid paginated metadata");
            }
            var rowNumber = 0;
            foreach (var row in (JsonArray)rows!)
            {
                rowNumber++;
                if (row is not JsonObject)
                {
                    throw new ValidationException($"GitHub returned invalid paginated metadata: expected an object at " +
                        $"page {pageNumber}, row {rowNumber} for {CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(endpoint, limit: 200))}");
                }
                result.Add(row.DeepClone());
            }
        }

        return result;
    }

    // Include passing and historical producers, but retain current-name precedence.
    // Historical context identifies reports; it never creates actionable failed jobs.
    internal async Task<TestArtifactExpectation[]> GetTestArtifactExpectationsForRunAsync(
        string repository, long runId, int runAttempt, JsonArray allJobs, IGitHubWorkflowConventions workflow)
    {
        IEnumerable<JsonObject> producerJobs = allJobs.OfType<JsonObject>();
        if (runAttempt > 1)
        {
            JsonArray historicalJobs;
            try
            {
                historicalJobs = await GetRowsAsync($"repos/{repository}/actions/runs/{runId.ToString(CultureInfo.InvariantCulture)}/jobs?filter=all", "jobs");
                if (historicalJobs.Count == 0 || historicalJobs.Any(job => string.IsNullOrEmpty(Text(Field(job, "name")))))
                {
                    throw new ValidationException("Historical job inventory is empty or contains unnamed producers.");
                }
            }
            catch (Exception error) when (error is ValidationException or JsonException or IOException)
            {
                throw new ValidationException($"Combined result producer ownership is unavailable: {error.Message}", error);
            }
            var currentNames = producerJobs.Select(job => Text(job["name"])).ToHashSet(StringComparer.Ordinal);
            producerJobs = producerJobs.Concat(historicalJobs.OfType<JsonObject>()
                .Where(job => !currentNames.Contains(Text(job["name"])))
                .DistinctBy(job => Text(job["name"]), StringComparer.Ordinal));
        }

        return producerJobs.SelectMany(job => workflow.DescribeTestArtifacts(job, runAttempt)).ToArray();
    }

    internal Task<JsonNode?> SendJsonAsync(HttpMethod method, string endpoint, JsonNode? body, HttpStatusCode expectedStatus)
        => TranslateAsync(() => _githubApi.SendJsonAsync(method, endpoint, body, expectedStatus, cancellationToken));

    internal async Task SendAsync(HttpMethod method, string endpoint, JsonNode? body, HttpStatusCode expectedStatus)
    {
        try
        {
            await _githubApi.SendAsync(method, endpoint, body, expectedStatus, cancellationToken);
        }
        catch (GitHubApiException error)
        {
            throw new ValidationException(error.Message, error);
        }
    }

    internal Task<GitHubTextDownload> DownloadTextAsync(string endpoint, long maximumBytes)
        => TranslateAsync(() => _githubApi.DownloadTextAsync(endpoint, maximumBytes, cancellationToken));

    internal Task<long> DownloadFileAsync(string endpoint, string outputPath, long maximumBytes)
        => TranslateAsync(() => _githubApi.DownloadFileAsync(endpoint, outputPath, maximumBytes, cancellationToken));

    private static async Task<T> TranslateAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (GitHubApiException error)
        {
            throw new ValidationException(error.Message, error);
        }
    }
}
