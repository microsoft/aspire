// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.TestUtilities;
using Xunit;
using Aspire.Templates.Tests;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Fixture that discovers a suitable merged PR with required CLI artifacts for integration testing.
/// Uses GitHub CLI and parses JSON in C# to avoid shell quoting issues.
/// </summary>
public class RealGitHubPRFixture : IAsyncLifetime
{
    private readonly ITestOutputHelper _testOutput;

    /// <summary>
    /// PR number that has the required artifacts.
    /// </summary>
    public int PRNumber { get; private set; }

    /// <summary>
    /// Workflow run ID for the PR.
    /// </summary>
    public long RunId { get; private set; }

    /// <summary>
    /// Commit SHA for the PR.
    /// </summary>
    public string CommitSHA { get; private set; } = string.Empty;

    public RealGitHubPRFixture()
    {
        // Note: In xUnit fixtures, we can't get ITestOutputHelper in constructor
        // We'll create one for initialization
        _testOutput = new TestOutputHelperStub();
    }

    public async ValueTask InitializeAsync()
    {
        // Check if GH_TOKEN is available
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(ghToken))
        {
            _testOutput.WriteLine("GH_TOKEN environment variable not set. Integration tests will be skipped.");
            _testOutput.WriteLine("To run integration tests, set GH_TOKEN to a valid GitHub token.");
            // Don't throw - let individual tests handle the missing fixture data
            PRNumber = 0;
            RunId = 0;
            CommitSHA = string.Empty;
            return;
        }

        // Query recent merged PRs using gh CLI
        // Use microsoft/aspire to match the default ASPIRE_REPO in the scripts
        var stdoutJson = await ExecuteGhJsonAsync(
            "pr", "list",
            "--repo", "microsoft/aspire",
            "--state", "merged",
            "--limit", "20",
            "--json", "number,mergedAt,headRefOid"
        );

        // Parse JSON response in C# to avoid jq quoting issues
        var prs = JsonSerializer.Deserialize<List<GitHubPR>>(stdoutJson)
            ?? throw new InvalidOperationException("Failed to parse PR list JSON");

        // Try each PR to find one with required artifacts
        foreach (var pr in prs.OrderByDescending(p => p.MergedAt))
        {
            if (await TryFindRunWithArtifactsAsync(pr.Number, pr.HeadRefOid))
            {
                PRNumber = pr.Number;
                return;
            }
        }

        throw new InvalidOperationException(
            "Could not find a suitable merged PR with required CLI artifacts. " +
            "This may indicate a CI/build issue. Please check recent PRs manually.");
    }

    private async Task<bool> TryFindRunWithArtifactsAsync(int prNumber, string commitSha)
    {
        // Query workflow runs for this PR's commit
        string runsJson;
        try
        {
            runsJson = await ExecuteGhJsonAsync(
                "run", "list",
                "--repo", "microsoft/aspire",
                "--commit", commitSha,
                "--workflow", "ci.yml",
                "--status", "completed",
                "--limit", "5",
                "--json", "databaseId,conclusion"
            );
        }
        catch (Exception ex) when (ex is ToolCommandException or JsonException or InvalidOperationException)
        {
            _testOutput.WriteLine($"Skipping PR {prNumber}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        var runs = JsonSerializer.Deserialize<List<GitHubWorkflowRun>>(runsJson);
        if (runs is null || runs.Count == 0)
        {
            return false;
        }

        // Find a successful run
        var successfulRun = runs.FirstOrDefault(r =>
            r.Conclusion?.Equals("success", StringComparison.OrdinalIgnoreCase) == true);

        if (successfulRun is null)
        {
            return false;
        }

        // Check if this run has required artifacts using the REST API with pagination
        // (gh run view --json artifacts is not supported; artifacts can span multiple pages)
        var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            string artifactsJson;
            try
            {
                artifactsJson = await ExecuteGhJsonAsync(
                    "api",
                    $"repos/microsoft/aspire/actions/runs/{successfulRun.DatabaseId}/artifacts?per_page={perPage}&page={page}"
                );
            }
            catch (Exception ex) when (ex is ToolCommandException or JsonException or InvalidOperationException)
            {
                _testOutput.WriteLine($"Skipping artifact query for run {successfulRun.DatabaseId}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            var artifactsResponse = JsonSerializer.Deserialize<ArtifactsResponse>(artifactsJson);
            if (artifactsResponse?.Artifacts is null || artifactsResponse.Artifacts.Count == 0)
            {
                break;
            }

            foreach (var artifact in artifactsResponse.Artifacts)
            {
                artifactNames.Add(artifact.Name);
            }

            if (artifactsResponse.Artifacts.Count < perPage)
            {
                break;
            }

            page++;
        }

        // Check for required artifacts
        var hasCliNativeArchives = artifactNames.Any(n => n.StartsWith("cli-native-", StringComparison.OrdinalIgnoreCase));
        var hasBuiltNugets = artifactNames.Contains("built-nugets");
        var hasBuiltNugetsRid = artifactNames.Any(n => n.StartsWith("built-nugets-for", StringComparison.OrdinalIgnoreCase));

        if (hasCliNativeArchives && hasBuiltNugets && hasBuiltNugetsRid)
        {
            RunId = successfulRun.DatabaseId;
            CommitSHA = commitSha;
            _testOutput.WriteLine($"Found suitable PR #{prNumber} with run {RunId}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Executes a gh CLI command and captures only stdout for JSON parsing,
    /// avoiding stderr pollution (upgrade notices, warnings) that would break deserialization.
    /// </summary>
    private async Task<string> ExecuteGhJsonAsync(params string[] args)
    {
        var stdoutLines = new List<string>();
        using var cmd = new ToolCommand("gh", _testOutput)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithOutputDataReceived(line =>
            {
                if (line is not null)
                {
                    stdoutLines.Add(line);
                }
            });

        var result = await cmd.ExecuteAsync(args);
        result.EnsureSuccessful();

        return string.Join(Environment.NewLine, stdoutLines);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Simple stub for initialization logging (ITestOutputHelper includes Write methods in xUnit v3)
    private sealed class TestOutputHelperStub : ITestOutputHelper
    {
        public string Output => string.Empty;
        public void Write(string message) => Console.Write(message);
        public void Write(string format, params object[] args) => Console.Write(format, args);
        public void WriteLine(string message) => Console.WriteLine(message);
        public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);
    }

    // JSON models for GitHub CLI responses
    private sealed class GitHubPR
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("mergedAt")]
        public DateTime MergedAt { get; set; }

        [JsonPropertyName("headRefOid")]
        public string HeadRefOid { get; set; } = string.Empty;
    }

    private sealed class GitHubWorkflowRun
    {
        [JsonPropertyName("databaseId")]
        public long DatabaseId { get; set; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }
    }

    private sealed class ArtifactsResponse
    {
        [JsonPropertyName("artifacts")]
        public List<Artifact>? Artifacts { get; set; }
    }

    private sealed class Artifact
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
