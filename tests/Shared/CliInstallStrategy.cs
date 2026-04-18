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
    /// Install via get-aspire-cli.sh with optional --quality or --version.
    /// Covers GA releases, daily builds, preview, and explicit versions.
    /// </summary>
    InstallScript,
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
        var workflowRunId = CliInstallStrategy.GetCliArchiveWorkflowRunId();

        return workflowRunId is null
            ? prNumber.ToString(CultureInfo.InvariantCulture)
            : $"{prNumber.ToString(CultureInfo.InvariantCulture)} --run-id {workflowRunId}";
    }

    internal static string QuoteBashArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
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
    internal const string CliArchiveWorkflowRunIdEnvironmentVariableName = "ASPIRE_CLI_WORKFLOW_RUN_ID";

    private const string PreinstalledEnvironmentVariableName = "ASPIRE_E2E_PREINSTALLED";
    private static readonly Regex s_versionPattern = new(@"^[0-9A-Za-z.\-]+$", RegexOptions.Compiled);

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

    private CliInstallStrategy(CliInstallMode mode, string? archivePath = null, CliInstallQuality? quality = null, string? version = null)
    {
        Mode = mode;
        ArchivePath = archivePath;
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
        if (!s_versionPattern.IsMatch(version))
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
    /// Creates an InstallScript strategy for the latest GA release.
    /// </summary>
    public static CliInstallStrategy LatestGa()
    {
        return new CliInstallStrategy(CliInstallMode.InstallScript);
    }

    /// <summary>
    /// Gets the workflow run ID that produced the CLI archive for the current test run, if one was provided.
    /// </summary>
    public static string? GetCliArchiveWorkflowRunId()
    {
        var runId = Environment.GetEnvironmentVariable(CliArchiveWorkflowRunIdEnvironmentVariableName);

        if (string.IsNullOrEmpty(runId))
        {
            return null;
        }

        if (!long.TryParse(runId, out _))
        {
            throw new ArgumentException($"{CliArchiveWorkflowRunIdEnvironmentVariableName} must be a valid integer, got: {runId}");
        }

        return runId;
    }

    /// <summary>
    /// Auto-detect the install strategy from the environment.
    /// Priority:
    ///   1. ASPIRE_E2E_ARCHIVE → LocalHive
    ///   2. ASPIRE_E2E_QUALITY → InstallScript with quality
    ///   3. ASPIRE_E2E_VERSION → InstallScript with version
    ///   4. ASPIRE_E2E_PREINSTALLED → Preinstalled
    ///   5. GITHUB_PR_NUMBER + GITHUB_PR_HEAD_SHA → PullRequest
    ///   6. CI/GITHUB_ACTIONS → InstallScript (dev/daily)
    ///   7. Local fallback → InstallScript (latest GA)
    /// </summary>
    public static CliInstallStrategy Detect()
    {
        var archivePath = Environment.GetEnvironmentVariable("ASPIRE_E2E_ARCHIVE");
        if (!string.IsNullOrEmpty(archivePath))
        {
            return FromLocalHive(archivePath);
        }

        var qualityStr = Environment.GetEnvironmentVariable("ASPIRE_E2E_QUALITY");
        if (!string.IsNullOrEmpty(qualityStr))
        {
            if (!Enum.TryParse<CliInstallQuality>(qualityStr, ignoreCase: true, out var quality))
            {
                throw new ArgumentException($"Invalid ASPIRE_E2E_QUALITY value: '{qualityStr}'. Must be one of: {string.Join(", ", Enum.GetNames<CliInstallQuality>())}");
            }

            return FromQuality(quality);
        }

        var version = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
        if (!string.IsNullOrEmpty(version))
        {
            return FromVersion(version);
        }

        var preinstalled = Environment.GetEnvironmentVariable(PreinstalledEnvironmentVariableName);
        if (string.Equals(preinstalled, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(preinstalled, "1", StringComparison.OrdinalIgnoreCase))
        {
            return Preinstalled();
        }

        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER");
        var headSha = Environment.GetEnvironmentVariable("GITHUB_PR_HEAD_SHA");
        if (!string.IsNullOrEmpty(prNumber) && !string.IsNullOrEmpty(headSha))
        {
            return FromPullRequest();
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
        {
            return FromQuality(CliInstallQuality.Dev);
        }

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

                var workflowRunId = GetCliArchiveWorkflowRunId();
                if (!string.IsNullOrEmpty(workflowRunId))
                {
                    config.Environment[CliArchiveWorkflowRunIdEnvironmentVariableName] = workflowRunId;
                }

                break;

            case CliInstallMode.InstallScript:
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
        return Mode switch
        {
            CliInstallMode.LocalHive => $"LocalHive ({ArchivePath})",
            CliInstallMode.Preinstalled => "Preinstalled (~/.aspire)",
            CliInstallMode.PullRequest => $"PullRequest (PR #{Environment.GetEnvironmentVariable("GITHUB_PR_NUMBER")})",
            CliInstallMode.InstallScript when Quality is not null => $"InstallScript (--quality {Quality.Value.ToString().ToLowerInvariant()})",
            CliInstallMode.InstallScript when Version is not null => $"InstallScript (--version {Version})",
            CliInstallMode.InstallScript => "InstallScript (latest GA)",
            _ => Mode.ToString(),
        };
    }
}
