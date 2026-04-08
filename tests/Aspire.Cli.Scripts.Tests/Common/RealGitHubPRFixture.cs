// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

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
        var cmd = new ToolCommand("gh", _testOutput)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await cmd.ExecuteAsync(
            "pr", "list",
            "--repo", "dotnet/aspire",
            "--state", "merged",
            "--limit", "20",
            "--json", "number,mergedAt,headRefOid"
        );

        result.EnsureSuccessful();

        // Parse JSON response in C# to avoid jq quoting issues
        var prs = JsonSerializer.Deserialize<List<GitHubPR>>(result.Output)
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
        var cmd = new ToolCommand("gh", _testOutput)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await cmd.ExecuteAsync(
            "run", "list",
            "--repo", "dotnet/aspire",
            "--commit", commitSha,
            "--workflow", "ci.yml",
            "--status", "completed",
            "--limit", "5",
            "--json", "databaseId,conclusion"
        );

        if (result.ExitCode != 0)
        {
            return false;
        }

        var runs = JsonSerializer.Deserialize<List<GitHubWorkflowRun>>(result.Output);
        if (runs == null || runs.Count == 0)
        {
            return false;
        }

        // Find a successful run
        var successfulRun = runs.FirstOrDefault(r => 
            r.Conclusion?.Equals("success", StringComparison.OrdinalIgnoreCase) == true);

        if (successfulRun == null)
        {
            return false;
        }

        // Check if this run has required artifacts
        var artifactsCmd = new ToolCommand("gh", _testOutput)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var artifactsResult = await artifactsCmd.ExecuteAsync(
            "run", "view", successfulRun.DatabaseId.ToString(),
            "--repo", "dotnet/aspire",
            "--json", "artifacts"
        );

        if (artifactsResult.ExitCode != 0)
        {
            return false;
        }

        var artifactsResponse = JsonSerializer.Deserialize<ArtifactsResponse>(artifactsResult.Output);
        if (artifactsResponse?.Artifacts == null)
        {
            return false;
        }

        var artifactNames = artifactsResponse.Artifacts.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
