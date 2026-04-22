// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Hex1b;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

/// <summary>
/// Helper methods for creating and managing Hex1b terminal sessions for deployment testing.
/// Extends the patterns from CLI E2E tests with deployment-specific functionality.
/// </summary>
internal static class DeploymentE2ETestHelpers
{
    /// <summary>
    /// Gets whether the tests are running in CI (GitHub Actions) vs locally.
    /// </summary>
    internal static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    /// <summary>
    /// Gets the PR number from the GITHUB_PR_NUMBER environment variable.
    /// When running locally (not in CI), returns 0.
    /// </summary>
    internal static int GetPrNumber()
    {
        var prNumberStr = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        if (string.IsNullOrEmpty(prNumberStr) || !int.TryParse(prNumberStr, out var prNumber))
        {
            return 0;
        }
        return prNumber;
    }

    /// <summary>
    /// Gets the commit SHA from the GITHUB_PR_HEAD_SHA environment variable.
    /// When running locally (not in CI), returns "local0000".
    /// </summary>
    internal static string GetCommitSha()
    {
        var commitSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");
        return string.IsNullOrEmpty(commitSha) ? "local0000" : commitSha;
    }

    /// <summary>
    /// Gets the GitHub Actions run ID from the GITHUB_RUN_ID environment variable.
    /// When running locally (not in CI), returns a timestamp-based ID.
    /// </summary>
    internal static string GetRunId()
    {
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        return string.IsNullOrEmpty(runId) ? DateTime.UtcNow.ToString("yyyyMMddHHmmss") : runId;
    }

    /// <summary>
    /// Gets the GitHub Actions run attempt from the GITHUB_RUN_ATTEMPT environment variable.
    /// When running locally (not in CI), returns "1".
    /// </summary>
    internal static string GetRunAttempt()
    {
        var runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        return string.IsNullOrEmpty(runAttempt) ? "1" : runAttempt;
    }

    /// <summary>
    /// Generates a unique resource group name for deployment tests.
    /// Format: e2e-[testcasename]-[runid]-[attempt]
    /// </summary>
    /// <param name="testCaseName">Short name for the test case (e.g., "starter", "python").</param>
    /// <returns>A unique resource group name.</returns>
    internal static string GenerateResourceGroupName(string testCaseName)
    {
        var runId = GetRunId();
        var attempt = GetRunAttempt();
        return $"e2e-{testCaseName}-{runId}-{attempt}";
    }

    /// <summary>
    /// Gets the install strategy for exercising the current build under deployment E2E.
    /// CI uses the CLI already preinstalled by the workflow, while local runs honor explicit strategy overrides.
    /// </summary>
    internal static CliInstallStrategy GetCurrentBuildCliInstallStrategy()
    {
        return IsRunningInCI ? CliInstallStrategy.Preinstalled() : CliInstallStrategy.Detect();
    }

    /// <summary>
    /// Creates a headless Hex1b terminal configured for deployment E2E testing with asciinema recording.
    /// Uses default dimensions of 160x48 unless overridden.
    /// </summary>
    /// <param name="testName">The test name used for the recording file path. Defaults to the calling method name.</param>
    /// <param name="width">The terminal width in columns. Defaults to 160.</param>
    /// <param name="height">The terminal height in rows. Defaults to 48.</param>
    /// <returns>A configured <see cref="Hex1bTerminal"/> instance. Caller is responsible for disposal.</returns>
    internal static Hex1bTerminal CreateTestTerminal(int width = 160, int height = 48, [CallerMemberName] string testName = "")
    {
        return Hex1bTestHelpers.CreateTestTerminal("aspire-deployment-e2e", width, height, testName);
    }

    /// <summary>
    /// Gets the path for storing asciinema recordings that will be uploaded as CI artifacts.
    /// </summary>
    internal static string GetTestResultsRecordingPath(string testName)
    {
        return Hex1bTestHelpers.GetTestResultsRecordingPath(testName, "aspire-deployment-e2e");
    }

    /// <summary>
    /// Gets the path for the GitHub step summary file.
    /// Returns null when not running in CI.
    /// </summary>
    internal static string? GetGitHubStepSummaryPath()
    {
        return Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    }
}
