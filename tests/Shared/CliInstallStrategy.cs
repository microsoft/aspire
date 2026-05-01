// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Hex1b;

namespace Aspire.Tests.Shared;

/// <summary>
/// Specifies how the Aspire CLI should be installed in E2E test environments.
/// </summary>
internal enum CliInstallMode
{
    /// <summary>
    /// Extract a localhive archive (.tar.gz) into the test environment.
    /// Set via ASPIRE_E2E_ARCHIVE.
    /// </summary>
    LocalHive,

    /// <summary>
    /// Use a CLI that has already been installed into the test environment.
    /// Intended for non-Docker scenarios where CI preinstalls the current build.
    /// </summary>
    Preinstalled,

    /// <summary>
    /// Install from PR build artifacts using get-aspire-cli-pr.sh.
    /// </summary>
    PullRequest,

    /// <summary>
    /// Install from pre-downloaded artifacts in a local directory using get-aspire-cli-pr.sh with --local-dir.
    /// Used for same-workflow scenarios (e.g., quarantine/outerloop) where artifacts are already on disk.
    /// </summary>
    LocalArchive,

    /// <summary>
    /// Install via get-aspire-cli.sh with optional --quality or --version.
    /// Covers GA releases, daily builds, preview, and explicit versions.
    /// </summary>
    InstallScript,

    /// <summary>
    /// Install via <c>dotnet tool install --global Aspire.Cli</c>.
    /// Supports published NuGet feeds and local nupkg sources.
    /// Set via ASPIRE_E2E_DOTNET_TOOL or ASPIRE_E2E_DOTNET_TOOL_SOURCE.
    /// </summary>
    DotnetTool,
}

/// <summary>
/// Shared shell commands used by the strategy-aware CLI install helpers.
/// </summary>
internal static class AspireCliShellCommandHelpers
{
    internal const string NumberedPromptSetupCommand = "CMDCOUNT=0; PROMPT_COMMAND='s=$?;((CMDCOUNT++));PS1=\"[$CMDCOUNT $([ $s -eq 0 ] && echo OK || echo ERR:$s)] \\$ \"'";
    internal const string CommonAspireEnvironmentAssignments = "ASPIRE_PLAYGROUND=true TERM=xterm DOTNET_CLI_TELEMETRY_OPTOUT=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_GENERATE_ASPNET_CERTIFICATE=false";
    internal const string AspireCliVersionCommand = "aspire --version";
    internal const string DockerInstallScriptCommandPrefix = "/opt/aspire-scripts/get-aspire-cli.sh";
    internal const string DockerPullRequestInstallCommandPrefix = "/opt/aspire-scripts/get-aspire-cli-pr.sh";
    internal const string DockerNuGetConfigPath = "/opt/aspire-scripts/NuGet.config";
    internal const string MainInstallScriptCommandPrefix = "curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli.sh | bash -s --";
    internal const string MainPullRequestInstallCommandPrefix = "curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/main/eng/scripts/get-aspire-cli-pr.sh | bash -s --";
    internal const string AkaMsInstallScriptCommandPrefix = "curl -fsSL https://aka.ms/aspire/get/install.sh | bash -s --";

    private static readonly string[] s_configureLocalHiveCommands =
    [
        "aspire config set channel local -g",
        "SDK_VER=$(ls ~/.aspire/hives/local/packages/Aspire.Hosting.*.nupkg 2>/dev/null | head -1 | sed 's/.*Aspire\\.Hosting\\.//;s/\\.nupkg//') && aspire config set sdk.version \"$SDK_VER\" -g"
    ];

    internal static string GetPrepareAspireEnvironmentCommand()
    {
        return $"export {CommonAspireEnvironmentAssignments}";
    }

    internal static string GetSourceAspireEnvironmentCommand(bool includeBundlePath)
    {
        return includeBundlePath
            ? $"export PATH=~/.aspire/bin:~/.aspire:$PATH {CommonAspireEnvironmentAssignments}"
            : $"export PATH=~/.aspire/bin:$PATH {CommonAspireEnvironmentAssignments}";
    }

    internal static string GetExtractLocalHiveArchiveCommand(string archivePath)
    {
        return $"mkdir -p ~/.aspire && tar -xzf {QuoteBashArg(archivePath)} -C ~/.aspire 2>/dev/null";
    }

    internal static IEnumerable<string> GetConfigureLocalHiveCommands()
    {
        return s_configureLocalHiveCommands;
    }

    internal static string GetInstallScriptCommand(CliInstallStrategy strategy, string commandPrefix)
    {
        return $"{commandPrefix}{GetInstallScriptArgs(strategy)}";
    }

    internal static string GetPullRequestInstallCommand(int prNumber, string commandPrefix)
    {
        return $"{commandPrefix} {GetPullRequestInstallArgs(prNumber)}";
    }

    internal static string GetBundlePullRequestInstallCommand(int prNumber)
    {
        return
            $"ref=$(gh api repos/microsoft/aspire/pulls/{prNumber} --jq '.head.sha') && " +
            $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/$ref/eng/scripts/get-aspire-cli-pr.sh | bash -s -- {GetPullRequestInstallArgs(prNumber)}";
    }

    internal static string GetInstallScriptArgs(CliInstallStrategy strategy)
    {
        if (strategy.Quality is not null)
        {
            return $" --quality {strategy.Quality.Value.ToString().ToLowerInvariant()}";
        }

        return strategy.Version is not null
            ? $" --version {strategy.Version}"
            : "";
    }

    internal static string GetPullRequestInstallArgs(int prNumber)
    {
        return prNumber.ToString(CultureInfo.InvariantCulture);
    }

    internal static string GetLocalArchiveInstallCommand(string localDir, string commandPrefix)
    {
        return $"{commandPrefix} --local-dir {QuoteBashArg(localDir)}";
    }

    internal static string GetLocalArchiveInstallCommandFromCurrentRef(string localDir)
    {
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "main";

        if (!Regex.IsMatch(sha, @"^[0-9a-fA-F]{7,40}$") && sha != "main")
        {
            throw new InvalidOperationException($"GITHUB_SHA contains an unexpected value: '{sha}'. Expected a hex commit SHA or 'main'.");
        }

        return $"curl -fsSL https://raw.githubusercontent.com/microsoft/aspire/{sha}/eng/scripts/get-aspire-cli-pr.sh | bash -s -- --local-dir {QuoteBashArg(localDir)}";
    }

    internal static string QuoteBashArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    internal static string GetDotnetToolInstallCommand(CliInstallStrategy strategy)
    {
        return $"dotnet tool install {GetDotnetToolInstallArgs(strategy, strategy.NupkgSourcePath)}";
    }

    internal static string GetDotnetToolInstallCommandInDocker(CliInstallStrategy strategy)
    {
        if (strategy.UsesStagingDotnetToolFeed)
        {
            return GetStagingDotnetToolInstallCommand(
                AspireCliShellCommandHelpers.DockerInstallScriptCommandPrefix,
                DockerNuGetConfigPath);
        }

        var nupkgSourcePath = strategy.NupkgSourcePath is not null ? "/tmp/aspire-nupkg-source" : null;
        var nuGetConfigPath = strategy.RequiresDotnetToolNuGetConfig ? DockerNuGetConfigPath : null;

        return $"dotnet tool install {GetDotnetToolInstallArgs(strategy, nupkgSourcePath, nuGetConfigPath)}";
    }

    internal static string GetStagingDotnetToolInstallCommand(string installScriptCommandPrefix, string baseNuGetConfigPath)
    {
        var script = $$"""
set -euo pipefail
TMP_DIR=$(mktemp -d)
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT
STAGING_INSTALL_DIR="$TMP_DIR/install-script"
STAGING_NUGET_CONFIG="$TMP_DIR/NuGet.config"
{{installScriptCommandPrefix}} --quality staging --install-path "$STAGING_INSTALL_DIR" --skip-path
STAGING_VERSION=$("$STAGING_INSTALL_DIR/aspire" --version)
STAGING_BASE_VERSION=${STAGING_VERSION%%+*}
STAGING_SHA=${STAGING_VERSION#*+}
if [ "$STAGING_SHA" = "$STAGING_VERSION" ] || [ -z "$STAGING_SHA" ]; then
  echo "Unable to derive staging feed SHA from Aspire CLI version '$STAGING_VERSION'." >&2
  exit 1
fi
STAGING_SHA8=${STAGING_SHA:0:8}
STAGING_FEED="https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-${STAGING_SHA8}/nuget/v3/index.json"
cp {{QuoteBashArg(baseNuGetConfigPath)}} "$STAGING_NUGET_CONFIG"
sed -i \
  -e "s|<add key=\"dotnet9\" value=\"[^\"]*\" />|<add key=\"aspire-staging\" value=\"$STAGING_FEED\" />|" \
  -e "s|<packageSource key=\"dotnet9\">|<packageSource key=\"aspire-staging\">|" \
  "$STAGING_NUGET_CONFIG"
echo "Resolved staging Aspire CLI version: $STAGING_VERSION"
echo "Resolved staging dotnet tool feed: $STAGING_FEED"
dotnet tool install --global Aspire.Cli --version "$STAGING_BASE_VERSION" --configfile "$STAGING_NUGET_CONFIG"
aspire config set channel staging -g
""";

        return $"bash -c {QuoteBashArg(script)}";
    }

    private static string GetDotnetToolInstallArgs(CliInstallStrategy strategy, string? nupkgSourcePath, string? nuGetConfigPath = null)
    {
        var args = "--global Aspire.Cli";

        if (strategy.Version is not null)
        {
            args += $" --version {strategy.Version}";
        }
        else if (strategy.IncludePrerelease)
        {
            args += " --prerelease";
        }

        if (nupkgSourcePath is not null)
        {
            args += $" --add-source {QuoteBashArg(nupkgSourcePath)}";
        }

        if (nuGetConfigPath is not null)
        {
            args += $" --configfile {QuoteBashArg(nuGetConfigPath)}";
        }

        return args;
    }
}

/// <summary>
/// Quality levels for the Aspire CLI install script.
/// </summary>
internal enum CliInstallQuality
{
    /// <summary>Daily development builds.</summary>
    Dev,

    /// <summary>Staging/preview builds.</summary>
    Staging,

    /// <summary>Official release builds.</summary>
    Release,
}

/// <summary>
/// Encapsulates how the Aspire CLI is detected, configured, and installed for test scenarios.
/// </summary>
internal sealed class CliInstallStrategy
{
    internal const string CliArchiveDirEnvironmentVariableName = "ASPIRE_E2E_CLI_ARCHIVE_DIR";

    private const string PreinstalledEnvironmentVariableName = "ASPIRE_E2E_PREINSTALLED";
    /// <summary>
    /// The install mode.
    /// </summary>
    public CliInstallMode Mode { get; }

    /// <summary>
    /// For LocalHive: the path to the .tar.gz archive on the host.
    /// </summary>
    public string? ArchivePath { get; }

    /// <summary>
    /// For InstallScript: the quality level.
    /// </summary>
    public CliInstallQuality? Quality { get; }

    /// <summary>
    /// For InstallScript: a specific version.
    /// </summary>
    public string? Version { get; }

    /// <summary>
    /// For LocalArchive: the directory containing pre-downloaded CLI archives and NuGet packages.
    /// </summary>
    public string? ArchiveDir { get; }

    /// <summary>
    /// For DotnetTool: path to directory containing nupkg files (local feed).
    /// </summary>
    public string? NupkgSourcePath { get; }

    /// <summary>
    /// For DotnetTool: include prerelease package versions when installing the latest available version.
    /// </summary>
    public bool IncludePrerelease { get; }

    /// <summary>
    /// Gets whether published DotnetTool installation should first resolve the staging CLI build and use its SHA-specific package feed.
    /// </summary>
    public bool UsesStagingDotnetToolFeed =>
        Mode is CliInstallMode.DotnetTool &&
        NupkgSourcePath is null &&
        Version is null &&
        Quality is CliInstallQuality.Staging;

    /// <summary>
    /// The expected CLI version after installation, when known.
    /// Set automatically for modes where the version is deterministic (LocalArchive, DotnetTool local source, explicit version).
    /// Used by post-install verification to assert the correct CLI binary was installed.
    /// </summary>
    public string? ExpectedVersion { get; }

    /// <summary>
    /// Gets whether the Docker dotnet tool install command needs the generated NuGet.config for internal/prerelease feeds.
    /// </summary>
    public bool RequiresDotnetToolNuGetConfig =>
        Mode is CliInstallMode.DotnetTool &&
        NupkgSourcePath is null &&
        (IncludePrerelease || IsPrereleaseVersion(Version));

    private CliInstallStrategy(CliInstallMode mode, string? archivePath = null, CliInstallQuality? quality = null, string? version = null, string? archiveDir = null, string? nupkgSourcePath = null, bool includePrerelease = false, string? expectedVersion = null)
    {
        Mode = mode;
        ArchivePath = archivePath;
        Quality = quality;
        Version = version;
        ArchiveDir = archiveDir;
        NupkgSourcePath = nupkgSourcePath;
        IncludePrerelease = includePrerelease;
        ExpectedVersion = expectedVersion;
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
    /// Creates a strategy for a CLI that is already installed in the test environment.
    /// </summary>
    public static CliInstallStrategy Preinstalled()
    {
        return new CliInstallStrategy(CliInstallMode.Preinstalled);
    }

    /// <summary>
    /// Creates an InstallScript strategy targeting a quality level.
    /// </summary>
    public static CliInstallStrategy FromQuality(CliInstallQuality quality)
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript, quality: quality);
    }

    /// <summary>
    /// Creates an InstallScript strategy targeting a specific version.
    /// </summary>
    public static CliInstallStrategy FromVersion(string version)
    {
        if (!CliPackageDiscovery.IsValidVersion(version))
        {
            throw new ArgumentException($"Invalid version format: '{version}'. Must contain only alphanumeric characters, dots, and dashes.", nameof(version));
        }

        return new CliInstallStrategy(CliInstallMode.InstallScript, version: version);
    }

    /// <summary>
    /// Creates a PullRequest strategy when the environment contains PR metadata.
    /// </summary>
    public static CliInstallStrategy FromPullRequest()
    {
        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        var headSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");

        if (string.IsNullOrEmpty(prNumber) || string.IsNullOrEmpty(headSha))
        {
            throw new InvalidOperationException("PullRequest strategy requires GITHUB_PR_NUMBER and GITHUB_PR_HEAD_SHA to be set.");
        }

        return new CliInstallStrategy(CliInstallMode.PullRequest);
    }

    /// <summary>
    /// Creates a LocalArchive strategy from a directory containing pre-downloaded CLI archives and NuGet packages.
    /// Automatically extracts the expected CLI version from the <c>Aspire.Cli.{version}.nupkg</c> file in the directory.
    /// </summary>
    public static CliInstallStrategy FromLocalArchive(string archiveDir)
    {
        if (!Directory.Exists(archiveDir))
        {
            throw new DirectoryNotFoundException($"LocalArchive directory not found: {archiveDir}");
        }

        var expectedVersion = CliPackageDiscovery.TryFindAspireCliPointerPackage(archiveDir)?.Version;
        return new CliInstallStrategy(CliInstallMode.LocalArchive, archiveDir: archiveDir, expectedVersion: expectedVersion);
    }

    /// <summary>
    /// Creates an InstallScript strategy for the latest GA release.
    /// </summary>
    public static CliInstallStrategy LatestGa()
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript);
    }

    /// <summary>
    /// Creates a DotnetTool strategy to install from a published NuGet feed.
    /// </summary>
    public static CliInstallStrategy FromDotnetTool(string? version = null, bool includePrerelease = false)
    {
        if (version is not null && !CliPackageDiscovery.IsValidVersion(version))
        {
            throw new ArgumentException($"Invalid version format: '{version}'. Must contain only alphanumeric characters, dots, and dashes.", nameof(version));
        }

        return new CliInstallStrategy(CliInstallMode.DotnetTool, version: version, includePrerelease: version is null && includePrerelease, expectedVersion: version);
    }

    /// <summary>
    /// Creates a DotnetTool strategy for a published NuGet feed.
    /// </summary>
    public static CliInstallStrategy FromPublishedDotnetToolFeed(string? version, CliInstallQuality? quality)
    {
        if (version is not null && !CliPackageDiscovery.IsValidVersion(version))
        {
            throw new ArgumentException($"Invalid version format: '{version}'. Must contain only alphanumeric characters, dots, and dashes.", nameof(version));
        }

        var includePrerelease = ShouldIncludePrereleaseForPublishedDotnetTool(version, quality);
        return new CliInstallStrategy(CliInstallMode.DotnetTool, quality: version is null ? quality : null, version: version, includePrerelease: version is null && includePrerelease, expectedVersion: version);
    }

    /// <summary>
    /// Creates a DotnetTool strategy to install from a local directory of nupkg files.
    /// </summary>
    public static CliInstallStrategy FromDotnetToolLocalSource(string nupkgSourcePath, string version)
    {
        if (!Directory.Exists(nupkgSourcePath))
        {
            throw new DirectoryNotFoundException($"Nupkg source directory not found: {nupkgSourcePath}");
        }

        if (!CliPackageDiscovery.IsValidVersion(version))
        {
            throw new ArgumentException($"Invalid version format: '{version}'. Must contain only alphanumeric characters, dots, and dashes.", nameof(version));
        }

        return new CliInstallStrategy(CliInstallMode.DotnetTool, version: version, nupkgSourcePath: nupkgSourcePath, expectedVersion: version);
    }

    /// <summary>
    /// Creates a DotnetTool strategy to install from a local directory of nupkg files, inferring
    /// the tool package version from the <c>Aspire.Cli.{version}.nupkg</c> package.
    /// </summary>
    public static CliInstallStrategy FromDotnetToolLocalSource(string nupkgSourcePath)
    {
        if (!Directory.Exists(nupkgSourcePath))
        {
            throw new DirectoryNotFoundException($"Nupkg source directory not found: {nupkgSourcePath}");
        }

        var version = CliPackageDiscovery.FindAspireCliPointerPackage(nupkgSourcePath).Version;
        return FromDotnetToolLocalSource(nupkgSourcePath, version);
    }

    /// <summary>
    /// Auto-detect the install strategy from the environment.
    /// Priority:
    ///   1. ASPIRE_E2E_ARCHIVE → LocalHive
    ///   2. ASPIRE_E2E_DOTNET_TOOL_SOURCE → DotnetTool with local nupkg source
    ///   3. ASPIRE_E2E_DOTNET_TOOL=true → DotnetTool from published feed
    ///   4. ASPIRE_E2E_QUALITY → InstallScript with quality
    ///   5. ASPIRE_E2E_VERSION → InstallScript with version
    ///   6. ASPIRE_E2E_PREINSTALLED → Preinstalled
    ///   7. ASPIRE_E2E_CLI_ARCHIVE_DIR → LocalArchive
    ///   8. GITHUB_PR_NUMBER + GITHUB_PR_HEAD_SHA → PullRequest
    ///   9. CI/GITHUB_ACTIONS → InstallScript (dev/daily)
    ///  10. Local fallback → InstallScript (latest GA)
    /// </summary>
    /// <param name="log">Optional log callback (e.g. <c>output.WriteLine</c>) for tracing the detection logic.</param>
    public static CliInstallStrategy Detect(Action<string>? log = null)
    {
        log?.Invoke("CLI install strategy detection starting...");

        var archivePath = Environment.GetEnvironmentVariable("ASPIRE_E2E_ARCHIVE");
        if (!string.IsNullOrEmpty(archivePath))
        {
            log?.Invoke($"  → Selected: LocalHive (ASPIRE_E2E_ARCHIVE={archivePath})");
            return FromLocalHive(archivePath);
        }

        var dotnetToolSource = Environment.GetEnvironmentVariable("ASPIRE_E2E_DOTNET_TOOL_SOURCE");
        if (!string.IsNullOrEmpty(dotnetToolSource))
        {
            var toolVersion = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
            var strategy = !string.IsNullOrEmpty(toolVersion)
                ? FromDotnetToolLocalSource(dotnetToolSource, toolVersion)
                : FromDotnetToolLocalSource(dotnetToolSource);

            log?.Invoke($"  → Selected: DotnetTool local source (ASPIRE_E2E_DOTNET_TOOL_SOURCE={dotnetToolSource}, version={strategy.Version})");
            return strategy;
        }

        var dotnetTool = Environment.GetEnvironmentVariable("ASPIRE_E2E_DOTNET_TOOL");
        if (string.Equals(dotnetTool, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dotnetTool, "1", StringComparison.OrdinalIgnoreCase))
        {
            var toolVersion = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
            var quality = GetQualityFromEnvironment();
            var strategy = FromPublishedDotnetToolFeed(toolVersion, quality);
            var versionDescription = toolVersion ?? strategy switch
            {
                { UsesStagingDotnetToolFeed: true } => "(staging)",
                { IncludePrerelease: true } => "(latest prerelease)",
                _ => "(latest stable)"
            };
            log?.Invoke($"  → Selected: DotnetTool published feed (ASPIRE_E2E_DOTNET_TOOL={dotnetTool}, version={versionDescription})");
            return strategy;
        }

        var qualityStr = GetQualityFromEnvironment();
        if (qualityStr is not null)
        {
            log?.Invoke($"  → Selected: InstallScript quality={qualityStr}");
            return FromQuality(qualityStr.Value);
        }

        var version = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
        if (!string.IsNullOrEmpty(version))
        {
            log?.Invoke($"  → Selected: InstallScript version={version}");
            return FromVersion(version);
        }

        var preinstalled = Environment.GetEnvironmentVariable(PreinstalledEnvironmentVariableName);
        if (string.Equals(preinstalled, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preinstalled, "1", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("  → Selected: Preinstalled");
            return Preinstalled();
        }

        var archiveDir = Environment.GetEnvironmentVariable(CliArchiveDirEnvironmentVariableName);
        if (!string.IsNullOrEmpty(archiveDir))
        {
            log?.Invoke($"  → Selected: LocalArchive ({CliArchiveDirEnvironmentVariableName}={archiveDir})");
            return FromLocalArchive(archiveDir);
        }

        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        var headSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");
        if (!string.IsNullOrEmpty(prNumber) && !string.IsNullOrEmpty(headSha))
        {
            log?.Invoke($"  → Selected: PullRequest (PR #{prNumber}, SHA={headSha})");
            return FromPullRequest();
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
        {
            log?.Invoke("  → Selected: InstallScript (CI fallback, quality=dev)");
            return FromQuality(CliInstallQuality.Dev);
        }

        log?.Invoke("  → Selected: InstallScript (local fallback, latest GA)");
        return LatestGa();
    }

    /// <summary>
    /// Configures a Docker container with the volumes, build args, and environment variables needed for this install mode.
    /// </summary>
    public void ConfigureContainer(DockerContainerOptions config)
    {
        config.BuildArgs["SKIP_SOURCE_BUILD"] = "true";

        switch (Mode)
        {
            case CliInstallMode.LocalHive:
                config.Volumes.Add($"{ArchivePath}:/tmp/aspire-localhive.tar.gz:ro");
                break;

            case CliInstallMode.Preinstalled:
                throw new InvalidOperationException("Preinstalled CLI mode is only supported for non-Docker test environments.");

            case CliInstallMode.PullRequest:
                var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
                if (!string.IsNullOrEmpty(ghToken))
                {
                    config.Environment["GH_TOKEN"] = ghToken;
                }

                config.Environment["GITHUB_PR_NUMBER"] = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER") ?? "";
                config.Environment["GITHUB_PR_HEAD_SHA"] = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA") ?? "";
                break;

            case CliInstallMode.LocalArchive:
                if (string.IsNullOrEmpty(ArchiveDir))
                {
                    throw new InvalidOperationException("LocalArchive mode requires ArchiveDir to be set.");
                }

                config.Volumes.Add($"{ArchiveDir}:/tmp/aspire-cli-archives:ro");
                break;

            case CliInstallMode.InstallScript:
                break;

            case CliInstallMode.DotnetTool:
                if (NupkgSourcePath is not null)
                {
                    config.Volumes.Add($"{NupkgSourcePath}:/tmp/aspire-nupkg-source:ro");
                }

                break;

            default:
                throw new InvalidOperationException($"Unknown install mode: {Mode}");
        }
    }

    /// <summary>
    /// Returns a description of this strategy for test output logging.
    /// </summary>
    public override string ToString()
    {
        var description = Mode switch
        {
            CliInstallMode.LocalHive => $"LocalHive ({ArchivePath})",
            CliInstallMode.Preinstalled => "Preinstalled (~/.aspire)",
            CliInstallMode.PullRequest => $"PullRequest (PR #{Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER")})",
            CliInstallMode.LocalArchive => $"LocalArchive ({ArchiveDir})",
            CliInstallMode.InstallScript when Quality is not null => $"InstallScript (--quality {Quality.Value.ToString().ToLowerInvariant()})",
            CliInstallMode.InstallScript when Version is not null => $"InstallScript (--version {Version})",
            CliInstallMode.InstallScript => "InstallScript (latest GA)",
            CliInstallMode.DotnetTool when NupkgSourcePath is not null => $"DotnetTool (local: {NupkgSourcePath}, --version {Version})",
            CliInstallMode.DotnetTool when Version is not null => $"DotnetTool (--version {Version})",
            CliInstallMode.DotnetTool when UsesStagingDotnetToolFeed => "DotnetTool (staging)",
            CliInstallMode.DotnetTool when IncludePrerelease => "DotnetTool (latest prerelease)",
            CliInstallMode.DotnetTool => "DotnetTool (latest)",
            _ => Mode.ToString(),
        };

        return ExpectedVersion is not null
            ? $"{description} [expected={ExpectedVersion}]"
            : description;
    }

    internal static CliInstallQuality? GetQualityFromEnvironment()
    {
        var qualityStr = Environment.GetEnvironmentVariable("ASPIRE_E2E_QUALITY");
        if (string.IsNullOrEmpty(qualityStr))
        {
            return null;
        }

        if (Enum.TryParse<CliInstallQuality>(qualityStr, ignoreCase: true, out var quality))
        {
            return quality;
        }

        throw new ArgumentException($"Invalid ASPIRE_E2E_QUALITY value: '{qualityStr}'. Must be one of: {string.Join(", ", Enum.GetNames<CliInstallQuality>())}");
    }

    private static bool ShouldIncludePrereleaseForPublishedDotnetTool(string? version, CliInstallQuality? quality)
    {
        return string.IsNullOrEmpty(version) && quality is CliInstallQuality.Dev;
    }

    private static bool IsPrereleaseVersion(string? version)
    {
        return version?.Contains('-', StringComparison.Ordinal) == true;
    }
}
