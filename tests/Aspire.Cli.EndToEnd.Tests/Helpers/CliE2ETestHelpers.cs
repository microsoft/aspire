// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Tests.Utils;
using Hex1b;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Helper methods for creating and managing Hex1b terminal sessions for Aspire CLI testing.
/// </summary>
internal static class CliE2ETestHelpers
{
    /// <summary>
    /// Gets whether the tests are running in CI (GitHub Actions) vs locally.
    /// When running locally, some commands are replaced with echo stubs.
    /// </summary>
    internal static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA"));

    /// <summary>
    /// Gets the PR number from the GITHUB_PR_NUMBER environment variable.
    /// When running locally (not in CI), returns a dummy value (0) for testing.
    /// </summary>
    /// <returns>The PR number, or 0 when running locally.</returns>
    internal static int GetRequiredPrNumber()
    {
        var prNumberStr = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");

        if (string.IsNullOrEmpty(prNumberStr))
        {
            // Running locally - return dummy value
            return 0;
        }

        Assert.True(int.TryParse(prNumberStr, out var prNumber), $"GITHUB_PR_NUMBER must be a valid integer, got: {prNumberStr}");
        return prNumber;
    }

    /// <summary>
    /// Gets the commit SHA from the GITHUB_PR_HEAD_SHA environment variable.
    /// This is the actual PR head commit, not the merge commit (GITHUB_SHA).
    /// When running locally (not in CI), returns a dummy value for testing.
    /// </summary>
    /// <returns>The commit SHA, or a dummy value when running locally.</returns>
    internal static string GetRequiredCommitSha()
    {
        var commitSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");

        if (string.IsNullOrEmpty(commitSha))
        {
            // Running locally - return dummy value
            return "local0000";
        }

        return commitSha;
    }

    /// <summary>
    /// Gets the path for storing asciinema recordings that will be uploaded as CI artifacts.
    /// In CI, this returns a path under $GITHUB_WORKSPACE/testresults/recordings/.
    /// Locally, this returns a path under the system temp directory.
    /// </summary>
    /// <param name="testName">The name of the test (used as the recording filename).</param>
    /// <returns>The full path to the .cast recording file.</returns>
    internal static string GetTestResultsRecordingPath(string testName)
    {
        return Hex1bTestHelpers.GetTestResultsRecordingPath(testName, "aspire-cli-e2e");
    }

    /// <summary>
    /// Creates a headless Hex1b terminal configured for E2E testing with asciinema recording.
    /// Uses default dimensions of 160x48 unless overridden.
    /// </summary>
    /// <param name="testName">The test name used for the recording file path. Defaults to the calling method name.</param>
    /// <param name="width">The terminal width in columns. Defaults to 160.</param>
    /// <param name="height">The terminal height in rows. Defaults to 48.</param>
    /// <returns>A configured <see cref="Hex1bTerminal"/> instance. Caller is responsible for disposal.</returns>
    internal static Hex1bTerminal CreateTestTerminal(int width = 160, int height = 48, [CallerMemberName] string testName = "")
    {
        return Hex1bTestHelpers.CreateTestTerminal("aspire-cli-e2e", width, height, testName);
    }

    /// <summary>
    /// Specifies how the Aspire CLI should be installed inside a Docker container.
    /// </summary>
    internal enum DockerInstallMode
    {
        /// <summary>
        /// The CLI was built from source by the Dockerfile and is already on PATH.
        /// </summary>
        SourceBuild,

        /// <summary>
        /// Install the latest GA release from aspire.dev.
        /// </summary>
        GaRelease,

        /// <summary>
        /// Install from PR artifacts using the get-aspire-cli-pr.sh script.
        /// </summary>
        PullRequest,
    }

    /// <summary>
    /// Specifies which Dockerfile variant to use for the test container.
    /// </summary>
    internal enum DockerfileVariant
    {
        /// <summary>
        /// .NET SDK + Docker + Python + Node.js. For tests that create/run .NET AppHosts.
        /// </summary>
        DotNet,

        /// <summary>
        /// Docker + Python + Node.js (no .NET SDK). For TypeScript-only AppHost tests.
        /// </summary>
        Polyglot,
    }

    /// <summary>
    /// Detects the install mode for Docker-based tests based on the current environment.
    /// </summary>
    /// <param name="repoRoot">The repo root directory on the host.</param>
    /// <returns>The detected <see cref="DockerInstallMode"/>.</returns>
    internal static DockerInstallMode DetectDockerInstallMode(string repoRoot)
    {
        if (IsRunningInCI)
        {
            return DockerInstallMode.PullRequest;
        }

        // Check if a locally-built native AOT CLI binary exists (developer has run ./build.sh --bundle).
        var cliPublishDir = FindLocalCliBinary(repoRoot);
        if (cliPublishDir is not null)
        {
            return DockerInstallMode.SourceBuild;
        }

        return DockerInstallMode.GaRelease;
    }

    /// <summary>
    /// Finds the locally-built native AOT CLI publish directory.
    /// Searches for the aspire binary under artifacts/bin/Aspire.Cli/*/net*/linux-x64/publish/.
    /// </summary>
    /// <returns>The publish directory path, or null if not found.</returns>
    internal static string? FindLocalCliBinary(string repoRoot)
    {
        var cliBaseDir = Path.Combine(repoRoot, "artifacts", "bin", "Aspire.Cli");
        if (!Directory.Exists(cliBaseDir))
        {
            return null;
        }

        // Search for the native AOT binary under any config/TFM combination.
        var matches = Directory.GetFiles(cliBaseDir, "aspire", SearchOption.AllDirectories)
            .Where(f => f.Contains("linux-x64") && f.Contains("publish"))
            .ToArray();

        return matches.Length > 0 ? Path.GetDirectoryName(matches[0]) : null;
    }

    /// <summary>
    /// Creates a Hex1b terminal that runs inside a Docker container built from the shared E2E Dockerfile.
    /// The Dockerfile builds the CLI from source (local dev) or accepts pre-built artifacts (CI).
    /// </summary>
    /// <param name="repoRoot">The repo root directory, used as the Docker build context.</param>
    /// <param name="installMode">The detected install mode, controlling Docker build args and volumes.</param>
    /// <param name="output">Test output helper for logging configuration details.</param>
    /// <param name="variant">Which Dockerfile variant to use (DotNet or Polyglot).</param>
    /// <param name="mountDockerSocket">Whether to mount the Docker socket for DCP/container access.</param>
    /// <param name="workspace">Optional workspace to mount into the container at /workspace.</param>
    /// <param name="width">Terminal width in columns.</param>
    /// <param name="height">Terminal height in rows.</param>
    /// <param name="testName">The test name for the recording file path.</param>
    /// <returns>A configured <see cref="Hex1bTerminal"/>. Caller is responsible for disposal.</returns>
    internal static Hex1bTerminal CreateDockerTestTerminal(
        string repoRoot,
        DockerInstallMode installMode,
        ITestOutputHelper output,
        DockerfileVariant variant = DockerfileVariant.DotNet,
        bool mountDockerSocket = false,
        TemporaryWorkspace? workspace = null,
        IEnumerable<string>? additionalVolumes = null,
        int width = 160,
        int height = 48,
        [CallerMemberName] string testName = "")
    {
        var recordingPath = GetTestResultsRecordingPath(testName);
        var dockerfileName = variant switch
        {
            DockerfileVariant.DotNet => "Dockerfile.e2e",
            DockerfileVariant.Polyglot => "Dockerfile.e2e-polyglot",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var dockerfilePath = Path.Combine(repoRoot, "tests", "Shared", "Docker", dockerfileName);

        output.WriteLine($"Creating Docker test terminal:");
        output.WriteLine($"  Test name:      {testName}");
        output.WriteLine($"  Install mode:   {installMode}");
        output.WriteLine($"  Variant:        {variant}");
        output.WriteLine($"  Dockerfile:     {dockerfilePath}");
        output.WriteLine($"  Workspace:      {workspace?.WorkspaceRoot.FullName ?? "(none)"}");
        output.WriteLine($"  Docker socket:  {mountDockerSocket}");
        output.WriteLine($"  Dimensions:     {width}x{height}");
        output.WriteLine($"  Recording:      {recordingPath}");

        var builder = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
            .WithAsciinemaRecording(recordingPath)
            .WithDockerContainer(c =>
            {
                c.DockerfilePath = dockerfilePath;
                c.BuildContext = repoRoot;

                if (mountDockerSocket)
                {
                    c.MountDockerSocket = true;
                }

                if (workspace is not null)
                {
                    // Mount using the same directory name so that
                    // workspace.WorkspaceRoot.Name matches inside the container
                    // (e.g., aspire CLI uses the dir name as the default project name).
                    c.Volumes.Add($"{workspace.WorkspaceRoot.FullName}:/workspace/{workspace.WorkspaceRoot.Name}");
                }

                if (additionalVolumes is not null)
                {
                    foreach (var volume in additionalVolumes)
                    {
                        c.Volumes.Add(volume);
                    }
                }

                // Always skip the expensive source build inside Docker.
                // For SourceBuild mode, the CLI is installed from a mounted local bundle.
                // For PullRequest/GaRelease, it's installed via scripts after container start.
                c.BuildArgs["SKIP_SOURCE_BUILD"] = "true";

                if (installMode == DockerInstallMode.SourceBuild)
                {
                    // Mount the locally-built native AOT CLI binary into the container.
                    var cliPublishDir = FindLocalCliBinary(repoRoot)
                        ?? throw new InvalidOperationException("SourceBuild mode detected but CLI binary not found");
                    c.Volumes.Add($"{cliPublishDir}:/opt/aspire-cli:ro");
                    output.WriteLine($"  CLI binary:     {cliPublishDir}");
                }

                if (installMode == DockerInstallMode.PullRequest)
                {
                    var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
                    if (!string.IsNullOrEmpty(ghToken))
                    {
                        c.Environment["GH_TOKEN"] = ghToken;
                    }

                    var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER") ?? "";
                    var prSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA") ?? "";
                    c.Environment["GITHUB_PR_NUMBER"] = prNumber;
                    c.Environment["GITHUB_PR_HEAD_SHA"] = prSha;
                    output.WriteLine($"  PR number:      {prNumber}");
                    output.WriteLine($"  PR head SHA:    {prSha}");
                }
            });

        return builder.Build();
    }

    /// <summary>
    /// Walks up from the test assembly directory to find the repo root (contains Aspire.slnx).
    /// </summary>
    internal static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repo root (directory containing Aspire.slnx) " +
            $"by walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Converts a host-side path (under the workspace root) to the corresponding
    /// container-side path (under /workspace/{workspaceName}). Use this when a path
    /// constructed from <see cref="TemporaryWorkspace.WorkspaceRoot"/> needs to be
    /// used in a command typed into the Docker container terminal.
    /// </summary>
    /// <param name="hostPath">The full host-side path.</param>
    /// <param name="workspace">The workspace whose root is mounted at /workspace/{name}.</param>
    /// <returns>The equivalent path inside the container.</returns>
    internal static string ToContainerPath(string hostPath, TemporaryWorkspace workspace)
    {
        var relativePath = Path.GetRelativePath(workspace.WorkspaceRoot.FullName, hostPath);
        return $"/workspace/{workspace.WorkspaceRoot.Name}/" + relativePath.Replace('\\', '/');
    }
}
