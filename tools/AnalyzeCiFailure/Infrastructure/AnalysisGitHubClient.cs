// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Net;
using Aspire.TestTools;

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
