// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Specifies how the Aspire CLI should be installed in the E2E test container.
/// </summary>
internal enum CliInstallMode
{
    /// <summary>
    /// Extract a localhive archive (.tar.gz) into the container.
    /// Set via ASPIRE_E2E_ARCHIVE env var.
    /// </summary>
    LocalHive,

    /// <summary>
    /// The CLI was built from source — native AOT binary is mounted from the host.
    /// Packages come from artifacts/packages/.
    /// </summary>
    SourceBuild,

    /// <summary>
    /// Install from PR build artifacts using get-aspire-cli-pr.sh.
    /// Used in CI when GITHUB_PR_NUMBER is set.
    /// </summary>
    PullRequest,

    /// <summary>
    /// Install via get-aspire-cli.sh with optional --quality or --version.
    /// Covers GA releases, daily builds, preview, etc.
    /// </summary>
    InstallScript,
}

/// <summary>
/// Encapsulates how the Aspire CLI is detected, configured, and installed
/// inside an E2E test Docker container. Replaces the scattered DockerInstallMode
/// branching across CliE2ETestHelpers and CliE2EAutomatorHelpers.
/// </summary>
internal sealed class CliInstallStrategy
{
    /// <summary>
    /// The install mode.
    /// </summary>
    public CliInstallMode Mode { get; }

    /// <summary>
    /// For LocalHive: the path to the .tar.gz archive on the host.
    /// </summary>
    public string? ArchivePath { get; }

    /// <summary>
    /// For SourceBuild: the path to the native AOT CLI publish directory on the host.
    /// </summary>
    public string? CliBinaryDir { get; }

    /// <summary>
    /// For InstallScript: the quality level (e.g., "dev", "staging", "release").
    /// </summary>
    public string? Quality { get; }

    /// <summary>
    /// For InstallScript: a specific version (e.g., "13.2.1").
    /// </summary>
    public string? Version { get; }

    private CliInstallStrategy(CliInstallMode mode, string? archivePath = null, string? cliBinaryDir = null, string? quality = null, string? version = null)
    {
        Mode = mode;
        ArchivePath = archivePath;
        CliBinaryDir = cliBinaryDir;
        Quality = quality;
        Version = version;
    }

    /// <summary>
    /// Creates a LocalHive strategy from an archive path.
    /// </summary>
    public static CliInstallStrategy FromLocalHive(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"LocalHive archive not found: {archivePath}");
        }

        return new CliInstallStrategy(CliInstallMode.LocalHive, archivePath: archivePath);
    }

    /// <summary>
    /// Creates an InstallScript strategy targeting a quality level (e.g., "dev" for daily builds).
    /// </summary>
    public static CliInstallStrategy FromQuality(string quality)
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript, quality: quality);
    }

    /// <summary>
    /// Creates an InstallScript strategy targeting a specific version.
    /// </summary>
    public static CliInstallStrategy FromVersion(string version)
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript, version: version);
    }

    /// <summary>
    /// Creates an InstallScript strategy for the latest GA release.
    /// </summary>
    public static CliInstallStrategy LatestGa()
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript);
    }

    /// <summary>
    /// Auto-detect the install strategy from the environment and local build artifacts.
    /// Priority:
    ///   1. ASPIRE_E2E_ARCHIVE → LocalHive
    ///   2. ASPIRE_E2E_QUALITY → InstallScript with quality
    ///   3. ASPIRE_E2E_VERSION → InstallScript with version
    ///   4. CI (GITHUB_PR_NUMBER) → PullRequest
    ///   5. Native AOT binary exists → SourceBuild
    ///   6. Fallback → InstallScript (latest GA)
    /// </summary>
    public static CliInstallStrategy Detect(string repoRoot)
    {
        // 1. Explicit archive override
        var archivePath = Environment.GetEnvironmentVariable("ASPIRE_E2E_ARCHIVE");
        if (!string.IsNullOrEmpty(archivePath))
        {
            return FromLocalHive(archivePath);
        }

        // 2. Explicit quality override (daily, staging, etc.)
        var quality = Environment.GetEnvironmentVariable("ASPIRE_E2E_QUALITY");
        if (!string.IsNullOrEmpty(quality))
        {
            return FromQuality(quality);
        }

        // 3. Explicit version override
        var version = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
        if (!string.IsNullOrEmpty(version))
        {
            return FromVersion(version);
        }

        // 4. CI — install from PR artifacts
        if (CliE2ETestHelpers.IsRunningInCI)
        {
            return new CliInstallStrategy(CliInstallMode.PullRequest);
        }

        // 5. Local source build — native AOT binary exists
        var cliBinaryDir = CliE2ETestHelpers.FindLocalCliBinary(repoRoot);
        if (cliBinaryDir is not null)
        {
            return new CliInstallStrategy(CliInstallMode.SourceBuild, cliBinaryDir: cliBinaryDir);
        }

        // 6. Fallback — latest GA
        return LatestGa();
    }

    /// <summary>
    /// Configures the Docker container builder with the appropriate volumes, build args,
    /// and environment variables for this install mode.
    /// </summary>
    public void ConfigureContainer(Hex1b.DockerContainerOptions config)
    {
        // Always skip the expensive source build inside Docker.
        config.BuildArgs["SKIP_SOURCE_BUILD"] = "true";

        switch (Mode)
        {
            case CliInstallMode.LocalHive:
                // Mount the archive into the container
                config.Volumes.Add($"{ArchivePath}:/tmp/aspire-localhive.tar.gz:ro");
                break;

            case CliInstallMode.SourceBuild:
                // Mount the locally-built CLI binary
                config.Volumes.Add($"{CliBinaryDir}:/opt/aspire-cli:ro");
                break;

            case CliInstallMode.PullRequest:
                var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
                if (!string.IsNullOrEmpty(ghToken))
                {
                    config.Environment["GH_TOKEN"] = ghToken;
                }

                config.Environment["GITHUB_PR_NUMBER"] = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER") ?? "";
                config.Environment["GITHUB_PR_HEAD_SHA"] = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA") ?? "";
                break;

            case CliInstallMode.InstallScript:
                // No special Docker config needed — install script runs inside container
                break;
        }
    }

    /// <summary>
    /// Returns a description of this strategy for test output logging.
    /// </summary>
    public override string ToString()
    {
        return Mode switch
        {
            CliInstallMode.LocalHive => $"LocalHive ({ArchivePath})",
            CliInstallMode.SourceBuild => $"SourceBuild ({CliBinaryDir})",
            CliInstallMode.PullRequest => $"PullRequest (PR #{Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER")})",
            CliInstallMode.InstallScript when Quality is not null => $"InstallScript (--quality {Quality})",
            CliInstallMode.InstallScript when Version is not null => $"InstallScript (--version {Version})",
            CliInstallMode.InstallScript => "InstallScript (latest GA)",
            _ => Mode.ToString(),
        };
    }
}
