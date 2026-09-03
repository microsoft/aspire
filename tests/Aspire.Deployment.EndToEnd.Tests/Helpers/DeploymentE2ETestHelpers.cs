// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
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
    /// Gets the commit SHA from the GITHUB_PR_HEAD_SHA environment variable,
    /// falling back to GITHUB_SHA for non-PR CI runs (e.g., schedule-triggered workflows).
    /// When running locally (not in CI), returns "local0000".
    /// </summary>
    internal static string GetCommitSha()
    {
        var commitSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");
        if (!string.IsNullOrEmpty(commitSha))
        {
            return commitSha;
        }

        var githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        return string.IsNullOrEmpty(githubSha) ? "local0000" : githubSha;
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

    /// <summary>
    /// Stops a detached AppHost out-of-band, without going through the test terminal.
    /// </summary>
    /// <remarks>
    /// Run-mode tests bind <c>terminal.RunAsync</c> to the test's cancellation token, so when the
    /// overall test timeout fires the terminal is gone and an <c>aspire stop</c> typed into it can
    /// never run — the detached AppHost would keep provisioning into a resource group that cleanup
    /// is about to delete. Launching the CLI directly keeps the stop working on that path, and gives
    /// it a timeout independent of the test budget. <c>StopCommand</c> locates the AppHost from the
    /// working directory rather than from terminal state, so running it out-of-band is equivalent.
    /// </remarks>
    internal static async Task StopAppHostAsync(string workspacePath, Action<string> log)
    {
        try
        {
            var cliPath = ResolveAspireCliPath();
            if (cliPath is null)
            {
                log("Skipping AppHost stop: could not locate the aspire CLI.");
                return;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "stop --non-interactive",
                    WorkingDirectory = workspacePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                log($"AppHost stop exited with code {process.ExitCode}.");
            }
            catch (OperationCanceledException)
            {
                // A hung stop must not block resource group cleanup, which is the step that actually
                // stops us paying for the deployment.
                process.Kill(entireProcessTree: true);
                log("AppHost stop timed out after 2 minutes and was killed.");
            }
        }
        catch (Exception ex)
        {
            log($"Failed to stop AppHost: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates the installed aspire CLI, checking the PR-isolated, current, and legacy install layouts.
    /// </summary>
    private static string? ResolveAspireCliPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new List<string>();

        // The PR install route isolates the CLI under a per-PR directory rather than the shared bin dir:
        //   compute_cli_install_dir() -> "$INSTALL_PREFIX/dogfood/pr-$PR_NUMBER/bin"  (INSTALL_PREFIX=$HOME/.aspire)
        // See eng/scripts/get-aspire-cli-pr.sh. CliInstallStrategy.Detect() selects that route whenever
        // GITHUB_PR_NUMBER and GITHUB_PR_HEAD_SHA are set, so probe it first: a PR-route machine may have
        // no CLI in the shared layout at all, and falling through would silently skip the AppHost stop.
        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        if (!string.IsNullOrEmpty(prNumber))
        {
            candidates.Add(Path.Combine(home, ".aspire", "dogfood", $"pr-{prNumber}", "bin", "aspire"));
        }

        candidates.Add(Path.Combine(home, ".aspire", "bin", "aspire"));
        candidates.Add(Path.Combine(home, ".aspire", "aspire"));

        return candidates.Find(File.Exists);
    }
}
