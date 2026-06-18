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
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Helper methods for creating and managing Hex1b terminal sessions for Aspire CLI testing.
/// </summary>
internal static class CliE2ETestHelpers
{
    internal const string CliArchiveDirEnvironmentVariableName = CliInstallStrategy.CliArchiveDirEnvironmentVariableName;
    internal const string DotNetImageEnvironmentVariableName = "ASPIRE_E2E_DOTNET_IMAGE";
    internal const string RequireDotNetImageEnvironmentVariableName = "ASPIRE_E2E_REQUIRE_DOTNET_IMAGE";
    internal const string PolyglotImageEnvironmentVariableName = "ASPIRE_E2E_POLYGLOT_IMAGE";
    internal const string RequirePolyglotImageEnvironmentVariableName = "ASPIRE_E2E_REQUIRE_POLYGLOT_IMAGE";
    internal const string PolyglotJavaImageEnvironmentVariableName = "ASPIRE_E2E_POLYGLOT_JAVA_IMAGE";
    internal const string RequirePolyglotJavaImageEnvironmentVariableName = "ASPIRE_E2E_REQUIRE_POLYGLOT_JAVA_IMAGE";
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
    /// Resolves the test method name for naming a recording file. See
    /// <see cref="Hex1bTestHelpers.ResolveTestMethodName"/> for the full rationale.
    /// </summary>
    private static string ResolveTestMethodName(string callerMemberName)
        => Hex1bTestHelpers.ResolveTestMethodName(callerMemberName);

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
        // Prefer the xUnit-reported test method name so that when a [Fact]/[Theory]
        // delegates into a private helper (e.g. *Core methods), the .cast file is
        // still named after the public test the TRX records an outcome for. Without
        // this, `[CallerMemberName]` captures the helper, the recording filename has
        // no matching TRX entry, and the recording-comment workflow tags the test as
        // "Unknown".
        var resolvedTestName = ResolveTestMethodName(testName);
        var recordingPath = GetTestResultsRecordingPath(resolvedTestName);
        RegisterCaptureFile("recording.cast", recordingPath);
        return Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(width, height)
            .WithAsciinemaRecording(recordingPath)
            .WithPtyProcess("/bin/bash", ["--norc"])
            .Build();
    }

    /// <summary>
    /// Starts the terminal run and returns a <see cref="TerminalRun"/> that captures diagnostics
    /// and exits the terminal on disposal.
    /// </summary>
    /// <param name="terminal">The Hex1b terminal to run.</param>
    /// <param name="workspace">The workspace for diagnostic capture.</param>
    /// <param name="automator">The automator used to drive the terminal.</param>
    /// <param name="counter">The sequence counter for prompt tracking.</param>
    /// <param name="cancellationToken">Cancellation token passed to <see cref="Hex1bTerminal.RunAsync"/>.</param>
    /// <returns>A <see cref="TerminalRun"/> that ensures diagnostics capture and clean exit on disposal.</returns>
    internal static TerminalRun StartRun(Hex1bTerminal terminal, TemporaryWorkspace workspace, Hex1bTerminalAutomator automator, SequenceCounter counter, ITestOutputHelper output, CancellationToken cancellationToken)
    {
        var pendingRun = terminal.RunAsync(cancellationToken);
        return new TerminalRun(pendingRun, automator, counter, workspace, output);
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
        string? network = null,
        int width = 160,
        int height = 48,
        [CallerMemberName] string testName = "")
    {
        // See CreateTestTerminal above for why we prefer the xUnit-reported test
        // method name over `[CallerMemberName]`.
        testName = ResolveTestMethodName(testName);
        var recordingPath = GetTestResultsRecordingPath(testName);
        RegisterCaptureFile("recording.cast", recordingPath);
        var dockerfilePath = GetDockerfilePath(repoRoot, variant);
        var prebuiltImageName = GetPrebuiltImageName(variant);

        if (variant is DockerfileVariant.PolyglotJava && prebuiltImageName is null)
        {
            EnsurePolyglotBaseImage(repoRoot, output);
        }

        output.WriteLine($"Creating Docker test terminal:");
        output.WriteLine($"  Test name:      {testName}");
        output.WriteLine($"  Strategy:       {strategy}");
        output.WriteLine($"  Expected ver:   {strategy.ExpectedVersion ?? "(not available)"}");
        output.WriteLine($"  Variant:        {variant}");
        output.WriteLine($"  Dockerfile:     {(prebuiltImageName is null ? dockerfilePath : "(prebuilt image)")}");
        output.WriteLine($"  Image:          {prebuiltImageName ?? "(build from Dockerfile)"}");
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
                ConfigureDockerContainerSource(c, repoRoot, variant);

                if (mountDockerSocket)
                {
                    c.MountDockerSocket = true;
                }

                if (network is not null)
                {
                    c.Network = network;
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

                ConfigureDockerContainerStrategy(c, strategy, prebuiltImageSelected: prebuiltImageName is not null);
            });

        return builder.Build();
    }

    internal static void ConfigureDockerContainerStrategy(DockerContainerOptions options, CliInstallStrategy strategy, bool prebuiltImageSelected = false)
    {
        // Delegate all mode-specific Docker config to the strategy.
        strategy.ConfigureContainer(options);

        if (prebuiltImageSelected)
        {
            if (!string.IsNullOrEmpty(options.DockerfilePath) || !string.IsNullOrEmpty(options.BuildContext))
            {
                throw new InvalidOperationException("A prebuilt CLI E2E image was selected, but Dockerfile configuration was also set. Prebuilt-image runs must not fall back to Dockerfile builds.");
            }

            options.BuildArgs.Clear();
        }
    }

    internal static void ConfigureDockerContainerSource(DockerContainerOptions options, string repoRoot, DockerfileVariant variant)
    {
        var prebuiltImageName = GetPrebuiltImageName(variant);
        if (prebuiltImageName is not null)
        {
            options.Image = prebuiltImageName;
            return;
        }

        if (variant is DockerfileVariant.DotNet && IsDotNetImageRequired())
        {
            throw new InvalidOperationException($"{DotNetImageEnvironmentVariableName} must be set when the prebuilt CLI E2E .NET image is required.");
        }

        if (variant is DockerfileVariant.Polyglot && IsPolyglotImageRequired())
        {
            throw new InvalidOperationException($"{PolyglotImageEnvironmentVariableName} must be set when the prebuilt CLI E2E polyglot image is required.");
        }

        if (variant is DockerfileVariant.PolyglotJava && IsPolyglotJavaImageRequired())
        {
            throw new InvalidOperationException($"{PolyglotJavaImageEnvironmentVariableName} must be set when the prebuilt CLI E2E Java image is required.");
        }

        options.DockerfilePath = GetDockerfilePath(repoRoot, variant);
        options.BuildContext = repoRoot;
    }

    private static string? GetPrebuiltImageName(DockerfileVariant variant)
    {
        var environmentVariableName = variant switch
        {
            DockerfileVariant.DotNet => DotNetImageEnvironmentVariableName,
            DockerfileVariant.Polyglot => PolyglotImageEnvironmentVariableName,
            DockerfileVariant.PolyglotJava => PolyglotJavaImageEnvironmentVariableName,
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var imageName = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(imageName) ? null : imageName.Trim();
    }

    private static string GetDockerfilePath(string repoRoot, DockerfileVariant variant)
    {
        var dockerfileName = variant switch
        {
            DockerfileVariant.DotNet => "Dockerfile.e2e",
            DockerfileVariant.Polyglot => "Dockerfile.e2e-polyglot-base",
            DockerfileVariant.PolyglotJava => "Dockerfile.e2e-polyglot-java",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        return Path.Combine(repoRoot, "tests", "Shared", "Docker", dockerfileName);
    }

    private static bool IsDotNetImageRequired()
    {
        return IsImageRequired(RequireDotNetImageEnvironmentVariableName);
    }

    private static bool IsPolyglotImageRequired()
    {
        return IsImageRequired(RequirePolyglotImageEnvironmentVariableName);
    }

    private static bool IsPolyglotJavaImageRequired()
    {
        return IsImageRequired(RequirePolyglotJavaImageEnvironmentVariableName);
    }

    private static bool IsImageRequired(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
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
        // See CreateTestTerminal above for why we prefer the xUnit-reported test
        // method name over `[CallerMemberName]`.
        testName = ResolveTestMethodName(testName);
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

    private static readonly HttpClient s_nuGetHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Queries nuget.org for the latest stable (non-prerelease) version of the Aspire project
    /// templates package. Used by emulated-released-build tests to coerce a locally built CLI into
    /// reporting and resolving against the latest shipped release via the <c>ASPIRE_CLI_*</c>
    /// identity overrides. Returns <see langword="null"/> when nuget.org cannot be reached or no
    /// stable version is found, so callers can <c>Assert.Skip</c> rather than fail on a network
    /// outage.
    /// </summary>
    internal static async Task<string?> TryGetLatestStableAspireVersionAsync(Action<string> log, CancellationToken cancellationToken)
    {
        // Package Base Address ("flat container") index for aspire.projecttemplates. Shape:
        //   { "versions": ["13.4.1", "13.4.2", "13.4.3", "13.5.0-preview.1.25600.1", ...] }
        // Versions are listed oldest-to-newest but we sort explicitly rather than trust order.
        // See https://learn.microsoft.com/nuget/api/package-base-address-resource.
        const string indexUrl = "https://api.nuget.org/v3-flatcontainer/aspire.projecttemplates/index.json";

        try
        {
            using var response = await s_nuGetHttpClient.GetAsync(indexUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                log("nuget.org flat-container index for aspire.projecttemplates had no 'versions' array.");
                return null;
            }

            Version? bestParsed = null;
            string? bestRaw = null;
            foreach (var element in versions.EnumerateArray())
            {
                var raw = element.GetString();
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                // A pre-release label ('-') or build metadata ('+') means it is not a shipped GA
                // release; only stable versions can stand in for "latest released build".
                if (raw.Contains('-', StringComparison.Ordinal) || raw.Contains('+', StringComparison.Ordinal))
                {
                    continue;
                }

                if (Version.TryParse(raw, out var parsed) && (bestParsed is null || parsed > bestParsed))
                {
                    bestParsed = parsed;
                    bestRaw = raw;
                }
            }

            if (bestRaw is null)
            {
                log("No stable aspire.projecttemplates version was found on nuget.org.");
            }

            return bestRaw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log($"Failed to query nuget.org for the latest stable Aspire version: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Identifies the latest published <c>staging</c> ("rc/daily") Aspire build: the SDK version it
    /// stamps and the source commit it was built from. Used by the emulated-staging E2E test to coerce
    /// a locally built CLI into reporting and resolving as that staging build via the
    /// <c>ASPIRE_CLI_*</c> identity overrides, so the staging feed-routing behavior (dropping a
    /// <c>NuGet.config</c> that maps <c>Aspire*</c> to the SHA-specific darc feed) can be validated
    /// without producing a real official build.
    /// </summary>
    /// <remarks>
    /// Discovery is intentionally lightweight (it never downloads the ~120&#160;MB CLI binary):
    /// <list type="number">
    /// <item>The <c>https://aka.ms/dotnet/9/aspire/rc/daily/...</c> download redirect is resolved to
    /// read the published version from the final ci.dot.net URL (e.g. <c>13.4.4</c>).</item>
    /// <item>Recent commits on the matching <c>release/&lt;major&gt;.&lt;minor&gt;</c> branch are
    /// listed via the GitHub API.</item>
    /// <item>The SHA-specific <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> feed for each commit
    /// (newest first) is probed until one is found that carries <c>Aspire.ProjectTemplates</c> at the
    /// published version. That commit is the staging build — verified empirically to match the commit
    /// baked into the published binary's informational version.</item>
    /// </list>
    /// Returns <see langword="null"/> on any failure (network, GitHub rate limit, not found) so callers
    /// can <c>Assert.Skip</c> rather than fail. The identity can also be pinned explicitly via the
    /// <c>ASPIRE_E2E_STAGING_VERSION</c> and <c>ASPIRE_E2E_STAGING_COMMIT</c> environment variables,
    /// which bypasses discovery entirely.
    /// </remarks>
    internal static async Task<StagingBuildIdentity?> TryGetLatestStagingBuildAsync(Action<string> log, CancellationToken cancellationToken)
    {
        // Escape hatch: an explicit pin via env vars bypasses all network discovery. Useful when the
        // GitHub API is rate-limited, or the latest staging build is older than the probe window.
        var pinnedVersion = Environment.GetEnvironmentVariable("ASPIRE_E2E_STAGING_VERSION");
        var pinnedCommit = Environment.GetEnvironmentVariable("ASPIRE_E2E_STAGING_COMMIT");
        if (!string.IsNullOrWhiteSpace(pinnedVersion) && !string.IsNullOrWhiteSpace(pinnedCommit))
        {
            log($"Using pinned staging identity from environment: version={pinnedVersion.Trim()}, commit={pinnedCommit.Trim()}");
            return new StagingBuildIdentity(pinnedVersion.Trim(), pinnedCommit.Trim());
        }

        try
        {
            var version = await ResolveLatestStagingVersionAsync(log, cancellationToken);
            if (version is null)
            {
                return null;
            }

            // Staging builds are published from a release branch. Derive release/<major>.<minor> from
            // the version so we only probe the commits that could have produced this build.
            var branchMatch = Regex.Match(version, @"^(?<major>\d+)\.(?<minor>\d+)\.");
            if (!branchMatch.Success)
            {
                log($"Could not derive a release branch from staging version '{version}'.");
                return null;
            }

            var branch = $"release/{branchMatch.Groups["major"].Value}.{branchMatch.Groups["minor"].Value}";
            var commits = await GetRecentBranchCommitShasAsync(branch, commitCount: 40, log, cancellationToken);
            if (commits.Count == 0)
            {
                log($"No commits returned for branch '{branch}'.");
                return null;
            }

            // Probe the SHA-specific darc feeds newest-first. The first commit whose feed carries
            // Aspire.ProjectTemplates whose release part matches the published version is the staging
            // build. The exact feed version is used as the emulated version so it matches the feed
            // packages whether the build is stable-shaped (13.4.4) or prerelease-shaped
            // (13.4.4-preview.*).
            foreach (var sha in commits)
            {
                var matchedVersion = await TryGetMatchingTemplateVersionAsync(sha, version, cancellationToken);
                if (matchedVersion is not null)
                {
                    log($"Resolved latest staging build: version={matchedVersion}, commit={sha} (branch {branch}).");
                    return new StagingBuildIdentity(matchedVersion, sha);
                }
            }

            log($"No darc feed carrying Aspire.ProjectTemplates {version} found in the last {commits.Count} commits of '{branch}'.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log($"Failed to discover the latest staging build: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the <c>https://aka.ms/dotnet/9/aspire/rc/daily</c> CLI download redirect and reads the
    /// published staging version from the final ci.dot.net URL. The request follows redirects but only
    /// reads response headers, so the large CLI archive body is never downloaded.
    /// </summary>
    private static async Task<string?> ResolveLatestStagingVersionAsync(Action<string> log, CancellationToken cancellationToken)
    {
        // The aka.ms link 301-redirects to the versioned blob, e.g.:
        //   https://ci.dot.net/public/aspire/13.4.4-preview.1.26310.6/aspire-cli-linux-x64-13.4.4.tar.gz
        // The file name carries the package-shaped version (13.4.4) that the darc feed and the CLI's
        // own stamp use; the directory segment is a CI-artifact version we ignore.
        const string stagingDownloadUrl = "https://aka.ms/dotnet/9/aspire/rc/daily/aspire-cli-linux-x64.tar.gz";

        using var response = await s_nuGetHttpClient.GetAsync(stagingDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (string.IsNullOrEmpty(finalUrl))
        {
            log("Could not determine the final URL for the staging CLI download redirect.");
            return null;
        }

        // aspire-cli-<rid>-<version>.tar.gz  (version may be stable- or prerelease-shaped)
        var match = Regex.Match(finalUrl, @"aspire-cli-[^/]+?-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\.tar\.gz", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            log($"Could not parse a staging version from '{finalUrl}'.");
            return null;
        }

        return match.Groups["version"].Value;
    }

    /// <summary>
    /// Lists the most recent commit SHAs on a branch via the GitHub commits API. Uses a token from
    /// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> when present to avoid the unauthenticated rate limit, but
    /// the microsoft/aspire repo is public so the call also works anonymously.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetRecentBranchCommitShasAsync(string branch, int commitCount, Action<string> log, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/microsoft/aspire/commits?sha={Uri.EscapeDataString(branch)}&per_page={commitCount}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub requires a User-Agent; the API also returns richer data with the modern Accept header.
        request.Headers.TryAddWithoutValidation("User-Agent", "aspire-cli-e2e-tests");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        var token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        using var response = await s_nuGetHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            log($"GitHub commits API for '{branch}' returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            log($"GitHub commits API for '{branch}' returned an unexpected payload.");
            return [];
        }

        var shas = new List<string>();
        foreach (var commit in document.RootElement.EnumerateArray())
        {
            if (commit.TryGetProperty("sha", out var sha) && sha.GetString() is { Length: > 0 } value)
            {
                shas.Add(value);
            }
        }

        return shas;
    }

    /// <summary>
    /// Returns the exact <c>Aspire.ProjectTemplates</c> version published to the
    /// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> feed for <paramref name="commitSha"/> whose
    /// release part (major.minor.patch) matches <paramref name="releaseVersion"/>, or
    /// <see langword="null"/> when no such feed/package exists. Matching on the release part (rather
    /// than the full string) lets a stable-shaped published version (13.4.4) resolve a prerelease-shaped
    /// feed package (13.4.4-preview.*) and vice versa.
    /// </summary>
    private static async Task<string?> TryGetMatchingTemplateVersionAsync(string commitSha, string releaseVersion, CancellationToken cancellationToken)
    {
        var shortSha = commitSha.Length >= 8 ? commitSha[..8].ToLowerInvariant() : commitSha.ToLowerInvariant();
        // NuGet v3 "flat container" (Package Base Address) index for the darc feed, e.g.:
        //   { "versions": ["13.4.4"] }
        var indexUrl = $"https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-{shortSha}/nuget/v3/flat2/aspire.projecttemplates/index.json";

        using var response = await s_nuGetHttpClient.GetAsync(indexUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The release part is everything before the first '-' (semver prerelease) or '+' (build metadata).
        static string ReleasePart(string v)
        {
            var end = v.IndexOfAny(['-', '+']);
            return end >= 0 ? v[..end] : v;
        }

        var releaseTarget = ReleasePart(releaseVersion);
        foreach (var element in versions.EnumerateArray())
        {
            if (element.GetString() is { Length: > 0 } candidate &&
                string.Equals(ReleasePart(candidate), releaseTarget, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
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

    private static void RegisterCaptureFile(string fileName, string path)
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

    /// <summary>
    /// Detects whether the host's packed Aspire packages contain a <b>stable-shaped</b> release version
    /// (e.g. <c>13.5.0</c>) and returns the highest such version, or <c>null</c> when only the usual
    /// pre-release packages (e.g. <c>13.5.0-dev</c>) are present.
    /// </summary>
    /// <remarks>
    /// This gates the all-local emulated-release E2E tests. Those tests only make sense when a developer
    /// has deliberately built a stable-shaped archive with <c>localhive --version X.Y.Z</c>; in every
    /// other configuration (default CI uses a pre-release <c>LocalArchive</c>; a normal local hive build
    /// produces a pre-release <c>-dev</c> version) there is no stable package to emulate and the test
    /// skips. A future release version such as <c>13.5.0</c> exists <em>only</em> in this local build, so
    /// a successful resolve against it later proves resolution came from the local packages rather than
    /// nuget.org.
    /// </remarks>
    internal static string? TryGetLocalStableAspireVersion(string repoRoot)
    {
        var shippingDirectories = new[]
        {
            Path.Combine(repoRoot, "artifacts", "packages", "Debug", "Shipping"),
            Path.Combine(repoRoot, "artifacts", "packages", "Release", "Shipping")
        };

        // A stable-shaped package is named exactly Aspire.Hosting.<major>.<minor>.<patch>.nupkg with no
        // pre-release label and no build metadata, e.g. "Aspire.Hosting.13.5.0.nupkg". The default
        // pre-release packages look like "Aspire.Hosting.13.5.0-dev.nupkg" and must NOT match.
        var stablePackageRegex = new Regex(@"^Aspire\.Hosting\.(?<version>\d+\.\d+\.\d+)\.nupkg$", RegexOptions.IgnoreCase);

        Version? bestParsed = null;
        string? bestRaw = null;
        foreach (var directory in shippingDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "Aspire.Hosting.*.nupkg", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = stablePackageRegex.Match(fileName);
                if (match.Success &&
                    Version.TryParse(match.Groups["version"].Value, out var parsed) &&
                    (bestParsed is null || parsed > bestParsed))
                {
                    bestParsed = parsed;
                    bestRaw = match.Groups["version"].Value;
                }
            }
        }

        return bestRaw;
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

/// <summary>
/// Identifies a published <c>staging</c> Aspire build for emulation: the SDK version it stamps and the
/// source commit it was built from. The CLI derives its SHA-specific
/// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> staging feed from the commit, so both values are
/// required to make a locally built CLI resolve <c>Aspire.*</c> packages exactly as that staging build
/// would. See <see cref="CliE2ETestHelpers.TryGetLatestStagingBuildAsync"/>.
/// </summary>
/// <param name="Version">The package-shaped SDK version (e.g. <c>13.4.4</c> or <c>13.4.4-preview.*</c>).</param>
/// <param name="Commit">The full source commit SHA the build was produced from.</param>
internal sealed record StagingBuildIdentity(string Version, string Commit)
{
    /// <summary>
    /// The first 8 lowercase hex characters of <see cref="Commit"/> — the segment the CLI embeds in the
    /// <c>darc-pub-microsoft-aspire-&lt;sha8&gt;</c> feed name.
    /// </summary>
    internal string ShortCommit => Commit.Length >= 8 ? Commit[..8].ToLowerInvariant() : Commit.ToLowerInvariant();
}
