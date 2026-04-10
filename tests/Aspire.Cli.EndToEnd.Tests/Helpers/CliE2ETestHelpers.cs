// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
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
    /// Gets the expected version string from the <c>ASPIRE_CLI_VERSION</c> environment variable,
    /// or <see langword="null"/> when running locally (version check is skipped).
    /// </summary>
    internal static string? ExpectedCliVersion =>
        Environment.GetEnvironmentVariable("ASPIRE_CLI_VERSION") is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Gets the directory that contains the pre-installed Aspire CLI binary, or
    /// <see langword="null"/> if the CLI was not pre-installed by the workflow.
    /// Set by the "Install CLI from archive" step in <c>run-tests.yml</c> when
    /// <c>requiresCliArchive</c> is <see langword="true"/> and there is no pull-request context
    /// (e.g., scheduled quarantine runs).
    /// </summary>
    internal static string? PreInstalledCliDir =>
        Environment.GetEnvironmentVariable("ASPIRE_CLI_PATH_DIR") is { Length: > 0 } v ? v : null;

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
        /// The CLI binary is pre-installed on the host and mounted into the container.
        /// Used for both local dev (./build.sh --bundle) and CI (PR, quarantine, outerloop).
        /// </summary>
        PreInstalled,

        /// <summary>
        /// Install the latest GA release from aspire.dev.
        /// </summary>
        GaRelease,
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
        /// Docker + Node.js (no .NET SDK). For Node-based polyglot AppHost tests.
        /// </summary>
        Polyglot,

        /// <summary>
        /// Docker + Node.js + Java (no .NET SDK). For Java polyglot AppHost tests.
        /// </summary>
        PolyglotJava,
    }

    private const string PolyglotBaseImageName = "aspire-e2e-polyglot-base";
    private static readonly object s_polyglotBaseImageLock = new();
    private static bool s_polyglotBaseImageBuilt;

    /// <summary>
    /// Detects the install mode for Docker-based tests based on the current environment.
    /// </summary>
    /// <param name="repoRoot">The repo root directory on the host.</param>
    /// <returns>The detected <see cref="DockerInstallMode"/>.</returns>
    internal static DockerInstallMode DetectDockerInstallMode(string repoRoot)
    {
        // Check if a pre-installed or locally-built CLI binary exists.
        var cliPublishDir = FindLocalCliBinary(repoRoot);
        if (cliPublishDir is not null)
        {
            return DockerInstallMode.PreInstalled;
        }

        return DockerInstallMode.GaRelease;
    }

    /// <summary>
    /// Finds the CLI binary directory to use for Docker PreInstalled mode.
    /// Checks, in order:
    /// <list type="number">
    /// <item>The <c>ASPIRE_CLI_PATH_DIR</c> env var set by the "Install CLI from archive" workflow step.</item>
    /// <item>The locally-built native AOT publish directory under <c>artifacts/bin/Aspire.Cli/</c>.</item>
    /// </list>
    /// Only Linux is supported (Docker-based E2E tests run on Linux only).
    /// </summary>
    /// <returns>The directory containing the <c>aspire</c> binary, or <see langword="null"/> if not found.</returns>
    internal static string? FindLocalCliBinary(string repoRoot)
    {
        // Docker E2E tests only run on Linux; the binary is named 'aspire' (no .exe).
        const string binaryName = "aspire";

        // Prefer the pre-installed CLI binary dir set by the CI workflow.
        var preInstalled = PreInstalledCliDir;
        if (preInstalled is not null && File.Exists(Path.Combine(preInstalled, binaryName)))
        {
            return preInstalled;
        }

        var cliBaseDir = Path.Combine(repoRoot, "artifacts", "bin", "Aspire.Cli");
        if (!Directory.Exists(cliBaseDir))
        {
            return null;
        }

        // Search for the native AOT binary under any config/TFM combination.
        var matches = Directory.GetFiles(cliBaseDir, binaryName, SearchOption.AllDirectories)
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
            DockerfileVariant.Polyglot => "Dockerfile.e2e-polyglot-base",
            DockerfileVariant.PolyglotJava => "Dockerfile.e2e-polyglot-java",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var dockerfilePath = Path.Combine(repoRoot, "tests", "Shared", "Docker", dockerfileName);

        if (variant is DockerfileVariant.PolyglotJava)
        {
            EnsurePolyglotBaseImage(repoRoot, output);
        }

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
                // For PreInstalled mode, the CLI is installed from a mounted local binary.
                // For GaRelease, it's installed via scripts after container start.
                c.BuildArgs["SKIP_SOURCE_BUILD"] = "true";

                if (installMode == DockerInstallMode.PreInstalled)
                {
                    // Mount the locally-built or CI-installed CLI binary into the container.
                    var cliPublishDir = FindLocalCliBinary(repoRoot)
                        ?? throw new InvalidOperationException("PreInstalled mode detected but CLI binary not found");
                    c.Volumes.Add($"{cliPublishDir}:/opt/aspire-cli:ro");
                    output.WriteLine($"  CLI binary:     {cliPublishDir}");

                    // Also mount the built NuGet packages so 'aspire new' / 'aspire add' / restore
                    // can resolve CI-built packages without going to nuget.org.
                    var builtNugetsPath = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");
                    if (!string.IsNullOrEmpty(builtNugetsPath) && Directory.Exists(builtNugetsPath))
                    {
                        c.Volumes.Add($"{builtNugetsPath}:/built-nugets:ro");
                        output.WriteLine($"  Built NuGets:   {builtNugetsPath}");
                    }
                }
            });

        return builder.Build();
    }

    private static void EnsurePolyglotBaseImage(string repoRoot, ITestOutputHelper output)
    {
        lock (s_polyglotBaseImageLock)
        {
            if (s_polyglotBaseImageBuilt)
            {
                return;
            }

            var dockerfilePath = Path.Combine(repoRoot, "tests", "Shared", "Docker", "Dockerfile.e2e-polyglot-base");

            output.WriteLine($"Building shared polyglot Docker base image from {dockerfilePath}");

            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--build-arg");
            startInfo.ArgumentList.Add("SKIP_SOURCE_BUILD=true");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(dockerfilePath);
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(PolyglotBaseImageName);
            startInfo.ArgumentList.Add(repoRoot);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start docker build process.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build shared polyglot Docker base image.{Environment.NewLine}" +
                    $"{standardOutput}{Environment.NewLine}{standardError}");
            }

            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                output.WriteLine(standardOutput.Trim());
            }

            s_polyglotBaseImageBuilt = true;
        }
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

    /// <summary>
    /// Copies a directory to testresults/workspaces/{testName}/{label} for CI artifact upload.
    /// Renames dot-prefixed directories to underscore-prefixed (upload-artifact skips hidden files).
    /// </summary>
    internal static void CaptureDirectory(string sourcePath, string testName, string? label)
    {
        var destDir = Path.Combine(
            AppContext.BaseDirectory,
            "TestResults",
            "workspaces",
            testName);

        if (label is not null)
        {
            destDir = Path.Combine(destDir, label);
        }

        using var logWriter = new StreamWriter(Path.Combine(
            Directory.CreateDirectory(destDir).FullName,
            "_capture.log"));

        CopyDirectory(sourcePath, destDir, line => logWriter.WriteLine(line));
    }

    private static void CopyDirectory(string sourceDir, string destDir, Action<string>? log)
    {
        Directory.CreateDirectory(destDir);

        log?.Invoke($"DIR: {sourceDir} ({Directory.GetFiles(sourceDir).Length} files, {Directory.GetDirectories(sourceDir).Length} dirs)");

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);

            // Skip node_modules — too large for artifacts
            if (dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"  SKIP: {dirName}");
                continue;
            }

            // Rename dot-prefixed dirs to underscore-prefixed
            // (upload-artifact uses include-hidden-files: false by default)
            var destDirName = dirName.StartsWith('.') ? "_" + dirName[1..] : dirName;
            CopyDirectory(dir, Path.Combine(destDir, destDirName), log);
        }
    }
}
