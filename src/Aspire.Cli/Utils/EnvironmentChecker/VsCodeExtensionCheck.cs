// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Reports whether the Aspire VS Code extension is installed and current.
/// </summary>
internal sealed class VsCodeExtensionCheck : IEnvironmentCheck
{
    internal const string CheckName = "vscode-extension";
    internal const string ExtensionId = "microsoft-aspire.aspire-vscode";
    internal const string MarketplaceUrl = "https://aka.ms/aspire/vscode-extension";
    internal const string ExtensionVersionEnvironmentVariable = "ASPIRE_VSCODE_EXTENSION_VERSION";
    internal const string ExtensionChannelEnvironmentVariable = "ASPIRE_VSCODE_EXTENSION_CHANNEL";
    internal const string ExtensionSourceEnvironmentVariable = "ASPIRE_VSCODE_EXTENSION_SOURCE";

    private readonly IEnvironment _environment;
    private readonly CliExecutionContext _executionContext;
    private readonly IVsCodeExtensionMarketplaceClient _marketplaceClient;
    private readonly ILogger<VsCodeExtensionCheck> _logger;
    private readonly Func<string, string?> _commandResolver;

    public VsCodeExtensionCheck(
        IEnvironment environment,
        CliExecutionContext executionContext,
        IVsCodeExtensionMarketplaceClient marketplaceClient,
        ILogger<VsCodeExtensionCheck> logger)
        : this(environment, executionContext, marketplaceClient, logger, PathLookupHelper.FindFullPathFromPath)
    {
    }

    internal VsCodeExtensionCheck(
        IEnvironment environment,
        CliExecutionContext executionContext,
        IVsCodeExtensionMarketplaceClient marketplaceClient,
        ILogger<VsCodeExtensionCheck> logger,
        Func<string, string?> commandResolver)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(marketplaceClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(commandResolver);

        _environment = environment;
        _executionContext = executionContext;
        _marketplaceClient = marketplaceClient;
        _logger = logger;
        _commandResolver = commandResolver;
    }

    public int Order => 60;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detection = Detect(_environment, _executionContext.HomeDirectory, _commandResolver);
        if (!detection.VsCodeInstalled)
        {
            return [];
        }

        var metadata = BuildMetadata(detection);
        if (!detection.ExtensionInstalled)
        {
            return
            [
                new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.DevelopmentTools,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = DoctorCommandStrings.VsCodeExtensionMissingMessage,
                    Fix = DoctorCommandStrings.VsCodeExtensionMissingFix,
                    Link = MarketplaceUrl,
                    Metadata = metadata
                }
            ];
        }

        if (!SemVersion.TryParse(detection.ExtensionVersion, SemVersionStyles.Strict, out var installedVersion))
        {
            metadata["extensionVersionKnown"] = false;
            return [CreateUnknownVersionResult(metadata)];
        }

        metadata["extensionVersionKnown"] = true;
        metadata["latestVersionKnown"] = false;

        // Disk discovery can identify an installed extension but not whether VS Code selected the
        // stable or prerelease Marketplace channel. No Marketplace result is actionable without
        // that channel, so avoid adding network latency or replacing this known limitation with a
        // misleading connectivity error.
        if (detection.ReleaseChannel == VsCodeExtensionReleaseChannel.Unknown)
        {
            return [CreateUnknownChannelResult(metadata)];
        }

        // Older extension versions do not report their editor product. Treat the missing source as
        // unknown rather than assuming Microsoft VS Code, so side-loaded installs in Code - OSS
        // products never receive an irrelevant Marketplace link.
        if (detection.ExtensionSource != VsCodeExtensionSource.MicrosoftMarketplace)
        {
            return [CreateUnknownSourceResult(metadata)];
        }

        try
        {
            var versions = await _marketplaceClient.GetLatestVersionsAsync(cancellationToken);
            var (latestVersion, channel) = GetLatestVersion(detection.ReleaseChannel, versions);
            if (latestVersion is null)
            {
                return [CreateLatestVersionNotFoundResult(metadata)];
            }

            var updateAvailable = SemVersion.ComparePrecedence(installedVersion, latestVersion) < 0;
            metadata["latestVersion"] = latestVersion.ToString();
            metadata["latestVersionChannel"] = channel;
            metadata["latestVersionKnown"] = true;
            metadata["updateAvailable"] = updateAvailable;

            if (!updateAvailable)
            {
                return [CreateInstalledResult(metadata)];
            }

            return
            [
                new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.DevelopmentTools,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(
                        CultureInfo.CurrentCulture,
                        DoctorCommandStrings.VsCodeExtensionOutOfDateMessageFormat,
                        detection.ExtensionVersion,
                        latestVersion),
                    Fix = DoctorCommandStrings.VsCodeExtensionOutOfDateFix,
                    Link = MarketplaceUrl,
                    Metadata = metadata
                }
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            _logger.LogDebug(exception, "The VS Code Marketplace version check was unavailable.");
            return [CreateMarketplaceUnavailableResult(metadata)];
        }
    }

    private static (SemVersion? Version, string Channel) GetLatestVersion(
        VsCodeExtensionReleaseChannel installedChannel,
        VsCodeExtensionMarketplaceVersions versions)
    {
        if (installedChannel == VsCodeExtensionReleaseChannel.Unknown)
        {
            return (null, "unknown");
        }

        if (installedChannel == VsCodeExtensionReleaseChannel.Stable)
        {
            return (versions.StableVersion, "stable");
        }

        return (versions.PreReleaseVersion, "prerelease");
    }

    internal static VsCodeExtensionDetection Detect(IEnvironment environment, DirectoryInfo homeDirectory)
        => Detect(environment, homeDirectory, PathLookupHelper.FindFullPathFromPath);

    internal static VsCodeExtensionDetection Detect(
        IEnvironment environment,
        DirectoryInfo homeDirectory,
        Func<string, string?> commandResolver)
    {
        var reportedVersion = environment.GetEnvironmentVariable(ExtensionVersionEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(reportedVersion))
        {
            return new VsCodeExtensionDetection(
                true,
                true,
                reportedVersion,
                ParseReleaseChannel(environment.GetEnvironmentVariable(ExtensionChannelEnvironmentVariable)),
                ParseExtensionSource(environment.GetEnvironmentVariable(ExtensionSourceEnvironmentVariable)));
        }

        var vsCodeInstalled = IsVsCodeInstalled(environment, homeDirectory, commandResolver);
        if (!vsCodeInstalled)
        {
            return new VsCodeExtensionDetection(false, false);
        }

        var extension = FindExtension(environment, homeDirectory);
        return new VsCodeExtensionDetection(
            true,
            extension.Found,
            extension.Version,
            VsCodeExtensionReleaseChannel.Unknown);
    }

    private static bool IsVsCodeInstalled(
        IEnvironment environment,
        DirectoryInfo homeDirectory,
        Func<string, string?> commandResolver)
    {
        if (string.Equals(environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // VS Code's macOS application does not install the `code` shell launcher unless the user
        // explicitly requests it, so also probe the standard system and per-user application roots.
        if (environment.IsMacOS() && IsMacOsApplicationInstalled(homeDirectory))
        {
            return true;
        }

        return commandResolver("code") is not null ||
            commandResolver("code-insiders") is not null;
    }

    private static bool IsMacOsApplicationInstalled(DirectoryInfo homeDirectory)
    {
        foreach (var applicationName in new[] { "Visual Studio Code.app", "Visual Studio Code - Insiders.app" })
        {
            if (Directory.Exists(Path.Combine("/Applications", applicationName)) ||
                Directory.Exists(Path.Combine(homeDirectory.FullName, "Applications", applicationName)))
            {
                return true;
            }
        }

        return false;
    }

    private static (bool Found, string? Version, SemVersion? ParsedVersion) FindExtension(
        IEnvironment environment,
        DirectoryInfo homeDirectory)
    {
        var selected = (Found: false, Version: (string?)null, ParsedVersion: (SemVersion?)null);
        foreach (var extensionsDirectory in GetExtensionDirectories(environment, homeDirectory))
        {
            var candidate = FindExtension(extensionsDirectory);
            if (candidate.Found &&
                (!selected.Found ||
                    candidate.ParsedVersion is not null &&
                    (selected.ParsedVersion is null ||
                        SemVersion.ComparePrecedence(candidate.ParsedVersion, selected.ParsedVersion) > 0)))
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private static IEnumerable<string> GetExtensionDirectories(IEnvironment environment, DirectoryInfo homeDirectory)
    {
        var overrideDirectory = environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            yield return overrideDirectory;
            yield break;
        }

        var home = homeDirectory.FullName;
        yield return Path.Combine(home, ".vscode", "extensions");
        yield return Path.Combine(home, ".vscode-insiders", "extensions");
        yield return Path.Combine(home, ".vscode-server", "extensions");
        yield return Path.Combine(home, ".vscode-server-insiders", "extensions");
    }

    private static (bool Found, string? Version, SemVersion? ParsedVersion) FindExtension(string extensionsDirectory)
    {
        if (!Directory.Exists(extensionsDirectory))
        {
            return (false, null, null);
        }

        try
        {
            var selected = (Found: false, Version: (string?)null, ParsedVersion: (SemVersion?)null);
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None
            };

            foreach (var directory in Directory.EnumerateDirectories(extensionsDirectory, "*", enumerationOptions))
            {
                var folderName = Path.GetFileName(directory);
                if (!IsVersionedExtensionFolder(folderName))
                {
                    continue;
                }

                var version = ReadExtensionVersion(directory);
                SemVersion.TryParse(version, SemVersionStyles.Strict, out var parsedVersion);
                if (!selected.Found ||
                    parsedVersion is not null &&
                    (selected.ParsedVersion is null ||
                        SemVersion.ComparePrecedence(parsedVersion, selected.ParsedVersion) > 0))
                {
                    selected = (true, version, parsedVersion);
                }
            }

            return selected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, null, null);
        }
    }

    private static string? ReadExtensionVersion(string extensionDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(extensionDirectory, "package.json");
            if (File.Exists(manifestPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("version", out var version) &&
                    version.ValueKind == JsonValueKind.String &&
                    SemVersion.TryParse(version.GetString(), SemVersionStyles.Strict, out _))
                {
                    return version.GetString();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the version encoded in the extension directory.
        }

        const string prefix = ExtensionId + "-";
        var folderVersion = Path.GetFileName(extensionDirectory)[prefix.Length..];
        return SemVersion.TryParse(folderVersion, SemVersionStyles.Strict, out _)
            ? folderVersion
            : null;
    }

    private static bool IsVersionedExtensionFolder(string folderName)
    {
        const string prefix = ExtensionId + "-";
        return folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            folderName.Length > prefix.Length &&
            char.IsAsciiDigit(folderName[prefix.Length]);
    }

    private static VsCodeExtensionReleaseChannel ParseReleaseChannel(string? channel)
        => channel?.Trim() switch
        {
            var value when string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase)
                => VsCodeExtensionReleaseChannel.Stable,
            var value when string.Equals(value, "prerelease", StringComparison.OrdinalIgnoreCase)
                => VsCodeExtensionReleaseChannel.PreRelease,
            _ => VsCodeExtensionReleaseChannel.Unknown
        };

    private static VsCodeExtensionSource ParseExtensionSource(string? source)
        => source?.Trim() switch
        {
            var value when string.Equals(value, "microsoft-marketplace", StringComparison.OrdinalIgnoreCase)
                => VsCodeExtensionSource.MicrosoftMarketplace,
            var value when string.Equals(value, "other", StringComparison.OrdinalIgnoreCase)
                => VsCodeExtensionSource.Other,
            _ => VsCodeExtensionSource.Unknown
        };

    private static EnvironmentCheckResult CreateInstalledResult(JsonObject metadata)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Pass,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Metadata = metadata
        };

    private static EnvironmentCheckResult CreateUnknownVersionResult(JsonObject metadata)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.VsCodeExtensionVersionUnknownMessage,
            Metadata = metadata
        };

    private static EnvironmentCheckResult CreateUnknownChannelResult(JsonObject metadata)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Details = DoctorCommandStrings.VsCodeExtensionLatestVersionCheckSkippedUnknownChannelDetails,
            Metadata = metadata
        };

    private static EnvironmentCheckResult CreateUnknownSourceResult(JsonObject metadata)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Details = DoctorCommandStrings.VsCodeExtensionLatestVersionCheckSkippedUnknownSourceDetails,
            Metadata = metadata
        };

    private static EnvironmentCheckResult CreateLatestVersionNotFoundResult(JsonObject metadata)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Details = DoctorCommandStrings.VsCodeExtensionLatestVersionNotFoundDetails,
            Metadata = metadata
        };

    private static EnvironmentCheckResult CreateMarketplaceUnavailableResult(JsonObject metadata)
    {
        metadata["latestVersionKnown"] = false;
        metadata["latestVersionError"] = "unavailable";

        return new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Details = DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails,
            Metadata = metadata
        };
    }

    private static JsonObject BuildMetadata(VsCodeExtensionDetection detection)
    {
        var metadata = new JsonObject
        {
            ["vsCodeInstalled"] = detection.VsCodeInstalled,
            ["extensionInstalled"] = detection.ExtensionInstalled,
            ["extensionId"] = ExtensionId
        };

        if (detection.ExtensionVersion is not null)
        {
            metadata["extensionVersion"] = detection.ExtensionVersion;
            metadata["extensionChannel"] = detection.ReleaseChannel switch
            {
                VsCodeExtensionReleaseChannel.Stable => "stable",
                VsCodeExtensionReleaseChannel.PreRelease => "prerelease",
                _ => "unknown"
            };
            metadata["extensionSource"] = detection.ExtensionSource switch
            {
                VsCodeExtensionSource.MicrosoftMarketplace => "microsoft-marketplace",
                VsCodeExtensionSource.Other => "other",
                _ => "unknown"
            };
        }

        return metadata;
    }
}

/// <summary>
/// The Marketplace release channel tracked by an Aspire VS Code extension installation.
/// </summary>
internal enum VsCodeExtensionReleaseChannel
{
    Unknown,
    Stable,
    PreRelease
}

/// <summary>
/// The extension gallery source inferred from the active editor product.
/// </summary>
internal enum VsCodeExtensionSource
{
    Unknown,
    MicrosoftMarketplace,
    Other
}

/// <summary>
/// The detected VS Code and Aspire extension state.
/// </summary>
internal sealed record VsCodeExtensionDetection(
    bool VsCodeInstalled,
    bool ExtensionInstalled,
    string? ExtensionVersion = null,
    VsCodeExtensionReleaseChannel ReleaseChannel = VsCodeExtensionReleaseChannel.Unknown,
    VsCodeExtensionSource ExtensionSource = VsCodeExtensionSource.Unknown);
