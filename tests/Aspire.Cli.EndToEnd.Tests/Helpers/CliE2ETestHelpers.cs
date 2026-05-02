// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Cli.Tests.Utils;
using Hex1b;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Helper methods for creating and managing Hex1b terminal sessions for Aspire CLI testing.
/// </summary>
internal static class CliE2ETestHelpers
{
    internal const string CliArchiveDirEnvironmentVariableName = CliInstallStrategy.CliArchiveDirEnvironmentVariableName;
    internal const string CliVersionOutputDirEnvironmentVariableName = "ASPIRE_E2E_CLI_VERSION_OUTPUT_DIR";
    internal const string ContainerCliVersionOutputDir = "/tmp/aspire-cli-versions";
    private static readonly Regex s_commitShaPattern = new("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);

    /// <summary>
    /// Gets whether the tests are running in CI (GitHub Actions) vs locally.
    /// When running locally, some commands are replaced with echo stubs.
    /// </summary>
    internal static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

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

    internal static bool IsPullRequestContext =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER")) ||
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME"), "pull_request", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetPullRequestHeadSha(out string commitSha)
    {
        commitSha = string.Empty;

        if (!IsPullRequestContext)
        {
            return false;
        }

        commitSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA") ?? string.Empty;
        if (string.IsNullOrEmpty(commitSha))
        {
            throw new InvalidOperationException("GITHUB_PR_HEAD_SHA must be set when running CLI E2E tests in pull request context.");
        }

        if (!s_commitShaPattern.IsMatch(commitSha))
        {
            throw new InvalidOperationException($"GITHUB_PR_HEAD_SHA must be a 40-character commit SHA, got: '{commitSha}'.");
        }

        return true;
    }

    /// <summary>
    /// Gets the path for storing asciinema recordings that will be uploaded as CI artifacts.
    /// In CI, this returns a path under $GITHUB_WORKSPACE/testresults/recordings/.
    /// Locally, this returns a path under the test output <c>TestResults/recordings/</c> directory.
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
        var recordingPath = GetTestResultsRecordingPath(testName);
        RegisterCaptureFile("recording.cast", recordingPath);
        return Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
            .WithAsciinemaRecording(recordingPath)
            .WithPtyProcess("/bin/bash", ["--norc"])
            .Build();
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
    private const string PodmanBaseImageName = "aspire-e2e-podman-base";
    private static readonly object s_polyglotBaseImageLock = new();
    private static readonly object s_podmanBaseImageLock = new();
    private static bool s_polyglotBaseImageBuilt;
    private static bool s_podmanBaseImageBuilt;

    /// <summary>
    /// Creates a Hex1b terminal that runs inside a Docker container, configured using the
    /// given <see cref="CliInstallStrategy"/> for CLI installation.
    /// </summary>
    /// <remarks>
    /// The install strategy decides how the CLI gets installed inside the container after startup. The container
    /// itself is still built from the repository Docker context, so <paramref name="repoRoot"/> is not specific to
    /// localhive scenarios.
    /// </remarks>
    internal static Hex1bTerminal CreateDockerTestTerminal(
        string repoRoot,
        CliInstallStrategy strategy,
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
        RegisterCaptureFile("recording.cast", recordingPath);
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
        output.WriteLine($"  Strategy:       {strategy}");
        output.WriteLine($"  Expected ver:   {strategy.ExpectedVersion ?? "(not available)"}");
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
                    c.Volumes.Add($"{workspace.WorkspaceRoot.FullName}:/workspace/{workspace.WorkspaceRoot.Name}");
                }

                var cliVersionOutputDir = Environment.GetEnvironmentVariable(CliVersionOutputDirEnvironmentVariableName);
                if (!string.IsNullOrEmpty(cliVersionOutputDir))
                {
                    Directory.CreateDirectory(cliVersionOutputDir);
                    c.Volumes.Add($"{cliVersionOutputDir}:{ContainerCliVersionOutputDir}");
                    c.Environment[CliVersionOutputDirEnvironmentVariableName] = ContainerCliVersionOutputDir;
                }

                if (additionalVolumes is not null)
                {
                    foreach (var volume in additionalVolumes)
                    {
                        c.Volumes.Add(volume);
                    }
                }

                // Delegate all mode-specific Docker config to the strategy
                strategy.ConfigureContainer(c);
            });

        return builder.Build();
    }

    /// <summary>
    /// Creates a Hex1b terminal backed by a privileged Docker container that runs Podman internally.
    /// </summary>
    /// <remarks>
    /// This is used for Podman deployment tests so the nested Podman runtime stays isolated from the host machine
    /// while still supporting the privileges required by Podman-in-container scenarios.
    /// </remarks>
    internal static Hex1bTerminal CreatePodmanDockerTestTerminal(
        string repoRoot,
        CliInstallStrategy strategy,
        ITestOutputHelper output,
        TemporaryWorkspace? workspace = null,
        IEnumerable<string>? additionalVolumes = null,
        int width = 160,
        int height = 48,
        [CallerMemberName] string testName = "")
    {
        var recordingPath = GetTestResultsRecordingPath(testName);
        RegisterCaptureFile("recording.cast", recordingPath);

        EnsurePodmanBaseImage(repoRoot, output);

        var containerName = GenerateDockerContainerName();
        var options = new DockerContainerOptions
        {
            AutoRemove = true,
            Image = PodmanBaseImageName,
            WorkingDirectory = "/workspace",
        };

        if (workspace is not null)
        {
            options.Volumes.Add($"{workspace.WorkspaceRoot.FullName}:/workspace/{workspace.WorkspaceRoot.Name}");
        }

        if (additionalVolumes is not null)
        {
            foreach (var volume in additionalVolumes)
            {
                options.Volumes.Add(volume);
            }
        }

        strategy.ConfigureContainer(options);

        output.WriteLine("Creating Podman Docker test terminal:");
        output.WriteLine($"  Test name:      {testName}");
        output.WriteLine($"  Strategy:       {strategy}");
        output.WriteLine($"  Image:          {PodmanBaseImageName}");
        output.WriteLine($"  Container name: {containerName}");
        output.WriteLine($"  Workspace:      {workspace?.WorkspaceRoot.FullName ?? "(none)"}");
        output.WriteLine($"  Dimensions:     {width}x{height}");
        output.WriteLine($"  Recording:      {recordingPath}");

        return Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
            .WithAsciinemaRecording(recordingPath)
            .WithPtyProcess("docker", BuildPrivilegedDockerRunArgs(options, containerName))
            .Build();
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
            AddUbuntuAptMirrorBuildArg(startInfo);
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

    private static void EnsurePodmanBaseImage(string repoRoot, ITestOutputHelper output)
    {
        lock (s_podmanBaseImageLock)
        {
            if (s_podmanBaseImageBuilt)
            {
                return;
            }

            var dockerfilePath = Path.Combine(repoRoot, "tests", "Shared", "Docker", "Dockerfile.e2e-podman");

            output.WriteLine($"Building shared Podman Docker base image from {dockerfilePath}");

            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("--quiet");
            AddUbuntuAptMirrorBuildArg(startInfo);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(dockerfilePath);
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(PodmanBaseImageName);
            startInfo.ArgumentList.Add(repoRoot);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start docker build process.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build shared Podman Docker base image.{Environment.NewLine}" +
                    $"{standardOutput}{Environment.NewLine}{standardError}");
            }

            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                output.WriteLine(standardOutput.Trim());
            }

            s_podmanBaseImageBuilt = true;
        }
    }

    private static string[] BuildPrivilegedDockerRunArgs(DockerContainerOptions options, string containerName)
    {
        var arguments = new List<string>
        {
            "run",
            "-it",
            "--privileged"
        };

        if (options.AutoRemove)
        {
            arguments.Add("--rm");
        }

        arguments.Add("--name");
        arguments.Add(containerName);

        foreach (var (key, value) in options.Environment)
        {
            arguments.Add("-e");
            arguments.Add($"{key}={value}");
        }

        foreach (var volume in options.Volumes)
        {
            arguments.Add("-v");
            arguments.Add(volume);
        }

        if (options.MountDockerSocket)
        {
            arguments.Add("-v");
            arguments.Add("/var/run/docker.sock:/var/run/docker.sock");
        }

        if (options.WorkingDirectory is not null)
        {
            arguments.Add("-w");
            arguments.Add(options.WorkingDirectory);
        }

        if (options.Network is not null)
        {
            arguments.Add("--network");
            arguments.Add(options.Network);
        }

        arguments.Add(options.Image);
        arguments.Add(options.Shell);
        arguments.AddRange(options.ShellArgs);

        return [.. arguments];
    }

    private static string GenerateDockerContainerName()
    {
        return $"hex1b-test-{Guid.NewGuid():N}".Substring(0, 32);
    }

    private static void AddUbuntuAptMirrorBuildArg(ProcessStartInfo startInfo)
    {
        var buildArgs = new Dictionary<string, string>();
        CliInstallStrategy.ConfigureUbuntuAptMirrorBuildArg(buildArgs);

        foreach (var (name, value) in buildArgs)
        {
            startInfo.ArgumentList.Add("--build-arg");
            startInfo.ArgumentList.Add($"{name}={value}");
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
    /// Gets a file path rooted under the mounted workspace.
    /// </summary>
    internal static string GetWorkspaceFilePath(TemporaryWorkspace workspace, string relativePath)
    {
        return Path.Combine(workspace.WorkspaceRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Verifies the persisted <c>aspire start --format json</c> transcript contains the expected contract fields.
    /// </summary>
    internal static void AssertAspireStartJsonContract(TemporaryWorkspace workspace)
    {
        var jsonPath = GetWorkspaceFilePath(workspace, "_aspire-start.json");
        WithJsonFailureDiagnostics("aspire start JSON", jsonPath, content =>
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            Assert.True(root.ValueKind is JsonValueKind.Object, $"Expected JSON object in {jsonPath}");
            Assert.True(root.TryGetProperty("appHostPath", out var appHostPath) &&
                appHostPath.ValueKind is JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(appHostPath.GetString()),
                $"Expected non-empty appHostPath in {jsonPath}");
            Assert.True(root.TryGetProperty("appHostPid", out var appHostPid) &&
                HasNonEmptyStringOrNumber(appHostPid),
                $"Expected non-empty appHostPid in {jsonPath}");
            Assert.True(root.TryGetProperty("dashboardUrl", out var dashboardUrl) &&
                dashboardUrl.ValueKind is JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(dashboardUrl.GetString()),
                $"Expected non-empty dashboardUrl in {jsonPath}");

            return true;
        });
    }

    /// <summary>
    /// Verifies the <c>aspire ps --format json</c> output contains at least one AppHost entry with the expected keys.
    /// </summary>
    internal static void AssertPsJsonContract(string jsonPath)
    {
        WithJsonFailureDiagnostics("aspire ps JSON", jsonPath, content =>
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            Assert.True(root.ValueKind is JsonValueKind.Array, $"Expected JSON array in {jsonPath}");

            var sawItem = false;
            foreach (var item in root.EnumerateArray())
            {
                sawItem = true;

                Assert.True(item.TryGetProperty("appHostPath", out var appHostPath) &&
                    appHostPath.ValueKind is JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(appHostPath.GetString()),
                    $"Expected non-empty appHostPath in {jsonPath}");
                Assert.True(item.TryGetProperty("appHostPid", out var appHostPid) &&
                    HasNonEmptyStringOrNumber(appHostPid),
                    $"Expected non-empty appHostPid in {jsonPath}");
            }

            Assert.True(sawItem, $"Expected at least one AppHost entry in {jsonPath}");
            return true;
        });
    }

    /// <summary>
    /// Extracts the first localhost resource endpoint URL from a JSON file.
    /// </summary>
    internal static string GetFirstLocalhostUrlFromJsonFile(string jsonPath)
    {
        return WithJsonFailureDiagnostics("resource JSON", jsonPath, content =>
        {
            using var document = JsonDocument.Parse(content);
            foreach (var resource in EnumerateResources(document.RootElement))
            {
                if (!resource.TryGetProperty("urls", out var urls) ||
                    urls.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var url in urls.EnumerateArray())
                {
                    var urlString = GetResourceUrlString(url);
                    if (urlString is not null &&
                        Uri.TryCreate(urlString, UriKind.Absolute, out var uri) &&
                        (uri.Scheme is "http" or "https") &&
                        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        return urlString;
                    }
                }
            }

            throw new InvalidOperationException($"Expected a localhost resource endpoint URL in {jsonPath}");
        });
    }

    private static IEnumerable<JsonElement> EnumerateResources(JsonElement root)
    {
        if (root.ValueKind is JsonValueKind.Object &&
            root.TryGetProperty("resources", out var resources) &&
            resources.ValueKind is JsonValueKind.Array)
        {
            foreach (var resource in resources.EnumerateArray())
            {
                yield return resource;
            }

            yield break;
        }

        if (root.ValueKind is JsonValueKind.Array)
        {
            foreach (var resource in root.EnumerateArray())
            {
                yield return resource;
            }

            yield break;
        }

        if (root.ValueKind is JsonValueKind.Object)
        {
            yield return root;
        }
    }

    private static string? GetResourceUrlString(JsonElement url)
    {
        if (url.ValueKind is JsonValueKind.String)
        {
            return url.GetString();
        }

        return url.ValueKind is JsonValueKind.Object &&
            url.TryGetProperty("url", out var value) &&
            value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool HasNonEmptyStringOrNumber(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => true,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => false
        };
    }

    private static T WithJsonFailureDiagnostics<T>(string label, string jsonPath, Func<string, T> assertion)
    {
        string? content = null;

        try
        {
            Assert.True(File.Exists(jsonPath), $"Expected JSON file at {jsonPath}");
            content = File.ReadAllText(jsonPath);
            return assertion(content);
        }
        catch
        {
            WriteJsonDiagnostic(label, jsonPath, content);
            throw;
        }
    }

    private static void WriteJsonDiagnostic(string label, string jsonPath, string? content)
    {
        var testContext = TestContext.Current;
        var header = $"=== {label}: {jsonPath} ===";
        var body = content ?? "<file missing or unreadable>";
        var footer = $"=== END {label} ===";

        if (testContext.TestOutputHelper is { } outputHelper)
        {
            outputHelper.WriteLine(header);
            outputHelper.WriteLine(body);
            outputHelper.WriteLine(footer);
            return;
        }

        testContext.SendDiagnosticMessage(header);
        testContext.SendDiagnosticMessage("{0}", body);
        testContext.SendDiagnosticMessage(footer);
    }

    /// <summary>
    /// Reads the VersionPrefix (e.g., "13.3.0") from eng/Versions.props by parsing
    /// the MajorVersion, MinorVersion, and PatchVersion MSBuild properties.
    /// </summary>
    internal static string GetVersionPrefix()
    {
        var repoRoot = GetRepoRoot();
        var versionsPropsPath = Path.Combine(repoRoot, "eng", "Versions.props");

        var doc = XDocument.Load(versionsPropsPath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        string? GetProperty(string name) =>
            doc.Descendants(ns + name).FirstOrDefault()?.Value;

        var major = GetProperty("MajorVersion")
            ?? throw new InvalidOperationException("MajorVersion not found in eng/Versions.props");
        var minor = GetProperty("MinorVersion")
            ?? throw new InvalidOperationException("MinorVersion not found in eng/Versions.props");
        var patch = GetProperty("PatchVersion")
            ?? throw new InvalidOperationException("PatchVersion not found in eng/Versions.props");

        return $"{major}.{minor}.{patch}";
    }

    /// <summary>
    /// Checks whether the build is stabilized (StabilizePackageVersion=true in eng/Versions.props).
    /// Stabilized builds produce version strings without commit SHA suffixes (e.g., "13.2.0" instead
    /// of "13.2.0-preview.1.25175.1+g{sha}"). This is only true for official release builds,
    /// never for normal PR CI builds.
    /// </summary>
    internal static bool IsStabilizedBuild()
    {
        var repoRoot = GetRepoRoot();
        var versionsPropsPath = Path.Combine(repoRoot, "eng", "Versions.props");

        var doc = XDocument.Load(versionsPropsPath);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // The default value in Versions.props uses a Condition to default to "false",
        // so we read the element's text directly.
        var stabilize = doc.Descendants(ns + "StabilizePackageVersion")
            .FirstOrDefault()?.Value;

        return string.Equals(stabilize, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static void RegisterCaptureFile(string fileName, string path)
    {
        if (TestContext.Current is null)
        {
            return;
        }

        TestContext.Current.KeyValueStorage[$"CaptureFile:{Path.GetFileName(fileName)}"] = path;
    }

    /// <summary>
    /// Prepares local channel metadata for source-build E2E tests.
    /// Validates that the expected packed Aspire.*.nupkg files exist and extracts the SDK version.
    /// Returns <c>null</c> when the CLI install strategy does not use a local hive archive.
    /// </summary>
    /// <param name="repoRoot">The repo root directory containing artifacts/.</param>
    /// <param name="strategy">The detected CLI install strategy.</param>
    /// <param name="requiredPackagePrefixes">
    /// Optional additional package name prefixes to validate beyond <c>Aspire.Hosting.</c>.
    /// For example, <c>["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript."]</c>.
    /// </param>
    /// <returns>A <see cref="LocalChannelInfo"/> with the SDK version, or <c>null</c> when the strategy is not local hive.</returns>
    internal static LocalChannelInfo? PrepareLocalChannel(
        string repoRoot,
        CliInstallStrategy strategy,
        string[]? requiredPackagePrefixes = null)
    {
        if (strategy.Mode != CliInstallMode.LocalHive)
        {
            return null;
        }

        return PrepareLocalChannelCore(repoRoot, requiredPackagePrefixes);
    }

    private static LocalChannelInfo PrepareLocalChannelCore(
        string repoRoot,
        string[]? requiredPackagePrefixes)
    {
        var shippingPackagesDirectory = new[]
        {
            Path.Combine(repoRoot, "artifacts", "packages", "Debug", "Shipping"),
            Path.Combine(repoRoot, "artifacts", "packages", "Release", "Shipping")
        }
        .FirstOrDefault(directory => Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, "Aspire*.nupkg", SearchOption.TopDirectoryOnly).Any());

        if (shippingPackagesDirectory is null)
        {
            throw new InvalidOperationException("Local source-built E2E tests require packed Aspire packages. Run './build.sh --bundle --pack' first.");
        }

        var allPackageFiles = Directory.EnumerateFiles(shippingPackagesDirectory, "Aspire*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(file => !file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var sdkVersion = allPackageFiles
            .Select(Path.GetFileName)
            .Where(static fileName => fileName is not null && Regex.IsMatch(fileName, @"^Aspire\.Hosting\.\d+\.\d+\.\d+.*\.nupkg$", RegexOptions.IgnoreCase))
            .Select(static fileName => fileName!["Aspire.Hosting.".Length..^".nupkg".Length])
            .OrderDescending(StringComparer.Ordinal)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(sdkVersion))
        {
            throw new InvalidOperationException("Local source-built E2E tests could not determine the Aspire SDK version from packed packages.");
        }

        var packageFiles = allPackageFiles
            .Where(file => file.EndsWith($"{sdkVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!packageFiles.Any(file => Path.GetFileName(file).StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Local source-built E2E tests require packed Aspire.Hosting packages. Run './build.sh --bundle --pack' first.");
        }

        if (requiredPackagePrefixes is not null)
        {
            foreach (var prefix in requiredPackagePrefixes)
            {
                if (!packageFiles.Any(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Local source-built E2E tests require packed {prefix.TrimEnd('.')} packages. Run './build.sh --bundle --pack' first.");
                }
            }
        }

        return new LocalChannelInfo(sdkVersion);
    }

    internal static void WriteLocalChannelSettings(string projectRoot, string sdkVersion)
    {
        var configPath = Path.Combine(projectRoot, "aspire.config.json");
        var config = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        config["channel"] = "local";
        config["sdk"] = new JsonObject
        {
            ["version"] = sdkVersion
        };

        File.WriteAllText(configPath, config.ToJsonString());
    }

    /// <summary>
    /// Information about a local NuGet package channel for source-build E2E tests.
    /// </summary>
    /// <param name="SdkVersion">The Aspire SDK version extracted from the package filenames.</param>
    internal sealed record LocalChannelInfo(string SdkVersion);

    /// <summary>
    /// Copies a directory to testresults/workspaces/{testName}/{label} for CI artifact upload.
    /// Renames dot-prefixed directories to underscore-prefixed (upload-artifact skips hidden files).
    /// </summary>
    internal static string CaptureDirectory(string sourcePath, string testName, string? label)
    {
        var destDir = GetCaptureRootDirectory(testName);

        if (label is not null)
        {
            destDir = Path.Combine(destDir, label);
        }

        using var logWriter = new StreamWriter(Path.Combine(
            Directory.CreateDirectory(destDir).FullName,
            "_capture.log"));

        CopyDirectory(sourcePath, destDir, line => logWriter.WriteLine(line));
        return destDir;
    }

    /// <summary>
    /// Copies a file to testresults/workspaces/{testName}/ for CI artifact upload.
    /// Hidden files are renamed to underscore-prefixed names for compatibility with artifact upload defaults.
    /// </summary>
    internal static string CaptureFile(string sourcePath, string testName, string fileName)
    {
        var destDir = Directory.CreateDirectory(GetCaptureRootDirectory(testName)).FullName;
        var captureFileName = Path.GetFileName(fileName);

        if (captureFileName.StartsWith(".", StringComparison.Ordinal))
        {
            captureFileName = "_" + captureFileName[1..];
        }

        var destFile = Path.Combine(destDir, captureFileName);
        File.Copy(sourcePath, destFile, overwrite: true);
        return destFile;
    }

    private static string GetCaptureRootDirectory(string testName)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "TestResults",
            "workspaces",
            testName);
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
