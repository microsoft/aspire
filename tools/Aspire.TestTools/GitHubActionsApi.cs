// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TestTools;

public sealed record GitHubActionsJob(
    long Id,
    string Name,
    string HtmlUrl,
    string? Conclusion,
    string? Status);

public sealed record GitHubActionsArtifact(
    long Id,
    string Name);

public static class GitHubActionsApi
{
    public static async Task<List<GitHubActionsJob>> ListJobsAsync(string repository, long runId, int? runAttempt, CancellationToken cancellationToken)
    {
        List<GitHubActionsJob> jobs = [];

        for (var page = 1; ; page++)
        {
            var endpoint = runAttempt is int attempt
                ? $"repos/{repository}/actions/runs/{runId}/attempts/{attempt}/jobs?per_page=100&page={page}"
                : $"repos/{repository}/actions/runs/{runId}/jobs?per_page=100&page={page}";

            using var jobsDocument = await GitHubCli.GetJsonAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var jobsArray = jobsDocument.RootElement.GetProperty("jobs");
            if (jobsArray.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var job in jobsArray.EnumerateArray())
            {
                jobs.Add(new GitHubActionsJob(
                    Id: job.GetProperty("id").GetInt64(),
                    Name: job.GetProperty("name").GetString() ?? $"job-{job.GetProperty("id").GetInt64()}",
                    HtmlUrl: job.TryGetProperty("html_url", out var htmlUrlElement) ? htmlUrlElement.GetString() ?? string.Empty : string.Empty,
                    Conclusion: job.TryGetProperty("conclusion", out var conclusionElement) ? conclusionElement.GetString() : null,
                    Status: job.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null));
            }

            if (jobsArray.GetArrayLength() < 100)
            {
                break;
            }
        }

        return jobs;
    }

    public static async Task<List<GitHubActionsArtifact>> ListArtifactsAsync(string repository, long runId, CancellationToken cancellationToken)
    {
        List<GitHubActionsArtifact> artifacts = [];

        for (var page = 1; ; page++)
        {
            using var artifactsDocument = await GitHubCli.GetJsonAsync(
                $"repos/{repository}/actions/runs/{runId}/artifacts?per_page=100&page={page}",
                cancellationToken).ConfigureAwait(false);

            var artifactsArray = artifactsDocument.RootElement.GetProperty("artifacts");
            if (artifactsArray.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var artifact in artifactsArray.EnumerateArray())
            {
                artifacts.Add(new GitHubActionsArtifact(
                    Id: artifact.GetProperty("id").GetInt64(),
                    Name: artifact.GetProperty("name").GetString() ?? $"artifact-{artifact.GetProperty("id").GetInt64()}"));
            }

            if (artifactsArray.GetArrayLength() < 100)
            {
                break;
            }
        }

        return artifacts;
    }

    public static Task<string> DownloadJobLogAsync(string repository, long jobId, CancellationToken cancellationToken)
        => GitHubCli.GetStringAsync($"repos/{repository}/actions/jobs/{jobId}/logs", cancellationToken);

    public static Task DownloadArtifactZipAsync(string repository, long artifactId, string outputPath, CancellationToken cancellationToken)
        => GitHubCli.DownloadFileAsync($"repos/{repository}/actions/artifacts/{artifactId}/zip", outputPath, cancellationToken);
}
