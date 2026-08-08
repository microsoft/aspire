// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks whether the Aspire VS Code extension is installed and current.
/// </summary>
/// <remarks>
/// The check is intentionally silent when VS Code is not detected. The installed version is taken
/// from the environment variable the extension itself contributes when the CLI runs inside a process
/// VS Code created for it, and otherwise resolved from the extension manifest on disk. The outcome is
/// three-state: a known current version passes, a known outdated version warns, and a version that
/// could not be determined warns separately rather than being reported as healthy.
/// </remarks>
internal sealed class VsCodeExtensionCheck : IEnvironmentCheck
{
    internal const string CheckName = "vscode-extension";

    /// <summary>
    /// The unique identifier of the Aspire VS Code extension (<c>&lt;publisher&gt;.&lt;name&gt;</c>).
    /// </summary>
    internal const string ExtensionId = "microsoft-aspire.aspire-vscode";

    /// <summary>
    /// The marketplace URL used as the fix link when the extension is missing. This is an aka.ms
    /// redirect so the ultimate destination can be updated without shipping a new CLI build.
    /// </summary>
    internal const string MarketplaceUrl = "https://aka.ms/aspire/vscode-extension";

    /// <summary>
    /// The environment variable the Aspire VS Code extension contributes to every terminal, task, and
    /// debug process it creates, carrying the version of the extension instance that is actually
    /// running. See <c>extension/src/utils/cliPathEnvironment.ts</c>.
    /// </summary>
    internal const string ExtensionVersionEnvironmentVariable = "ASPIRE_VSCODE_EXTENSION_VERSION";

    private const string StableChannel = "stable";
    private const string PreReleaseChannel = "pre-release";

    private const int MaximumManifestSize = 1024 * 1024;

    private readonly IEnvironment _environment;
    private readonly CliExecutionContext _executionContext;
    private readonly IVsCodeExtensionMarketplaceClient _marketplaceClient;
    private readonly IFeatures? _features;
    private readonly ILogger<VsCodeExtensionCheck> _logger;
    private readonly Func<string, string?> _commandResolver;

    public VsCodeExtensionCheck(
        IEnvironment environment,
        CliExecutionContext executionContext,
        IVsCodeExtensionMarketplaceClient marketplaceClient,
        IFeatures features,
        ILogger<VsCodeExtensionCheck> logger)
        : this(
            environment,
            executionContext,
            marketplaceClient,
            features,
            logger,
            PathLookupHelper.FindFullPathFromPath)
    {
    }

    internal VsCodeExtensionCheck(
        IEnvironment environment,
        CliExecutionContext executionContext,
        IVsCodeExtensionMarketplaceClient marketplaceClient,
        ILogger<VsCodeExtensionCheck> logger,
        Func<string, string?> commandResolver)
        : this(environment, executionContext, marketplaceClient, features: null, logger, commandResolver)
    {
    }

    internal VsCodeExtensionCheck(
        IEnvironment environment,
        CliExecutionContext executionContext,
        IVsCodeExtensionMarketplaceClient marketplaceClient,
        IFeatures? features,
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
        _features = features;
        _logger = logger;
        _commandResolver = commandResolver;
    }

    // Runs after the fast environment and OS checks. The Marketplace lookup carries its own
    // timeout so a slow network cannot hold the whole doctor run open.
    public int Order => 60;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detection = Detect(_environment, _executionContext.HomeDirectory, _commandResolver);
        if (!detection.VsCodeInstalled)
        {
            return [];
        }

        var updateCheckEnabled =
            _features?.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, defaultValue: true)
            ?? true;
        var metadata = BuildMetadata(detection, updateCheckEnabled);

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

        // A disabled update check is a deliberate opt-out, so it reports the same "installed" pass it
        // did before the comparison existed. An unknown version is different: doctor is a diagnostic
        // command, so reporting "healthy" when the version could not be read would end the user's
        // investigation on absent evidence. That case gets its own warning instead.
        if (!updateCheckEnabled)
        {
            return [CreateInstalledResult(metadata, EnvironmentCheckStatus.Pass)];
        }

        if (!SemVersion.TryParse(detection.ExtensionVersion, SemVersionStyles.Strict, out var installedVersion))
        {
            metadata["extensionVersionKnown"] = false;

            return
            [
                new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.DevelopmentTools,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = DoctorCommandStrings.VsCodeExtensionVersionUnknownMessage,
                    Details = FormatSearchedRoots(detection.SearchedRoots),
                    Fix = DoctorCommandStrings.VsCodeExtensionVersionUnknownFix,
                    Link = MarketplaceUrl,
                    Metadata = metadata
                }
            ];
        }

        metadata["extensionVersionKnown"] = true;

        VsCodeExtensionMarketplaceVersions versions;
        try
        {
            versions = await _marketplaceClient.GetLatestVersionsAsync(cancellationToken);
        }
        catch (TimeoutException exception)
        {
            _logger.LogDebug(exception, "The VS Code Marketplace version check timed out.");
            metadata["latestVersionError"] = "timeout";

            return
            [
                CreateInstalledResult(
                    metadata,
                    EnvironmentCheckStatus.Warning,
                    DoctorCommandStrings.VsCodeExtensionLatestVersionCheckTimedOutDetails)
            ];
        }
        catch (HttpRequestException exception)
        {
            return [CreateMarketplaceUnavailableResult(metadata, exception)];
        }
        catch (IOException exception)
        {
            return [CreateMarketplaceUnavailableResult(metadata, exception)];
        }
        catch (InvalidDataException exception)
        {
            // The Marketplace was reachable, but the response shape or size made the version data
            // unusable. Treat that the same as unavailable external data so doctor keeps running.
            return [CreateMarketplaceUnavailableResult(metadata, exception)];
        }
        catch (JsonException exception)
        {
            return [CreateMarketplaceUnavailableResult(metadata, exception)];
        }

        // The extension host API exposes the manifest version but not the gallery's pre-release flag,
        // so the channel is inferred from the version itself. Daily and PR builds carry a semver
        // pre-release tag and compare against the pre-release feed. A gallery pre-release install
        // published without such a tag compares against stable, which is safe: the gallery requires a
        // pre-release version to sort above the stable one, so the comparison passes instead of
        // nagging.
        var channel = installedVersion.IsPrerelease ? PreReleaseChannel : StableChannel;
        var latestVersion = installedVersion.IsPrerelease ? versions.PreReleaseVersion : versions.StableVersion;
        if (latestVersion is null)
        {
            // Comparing a pre-release install against the stable feed (or vice versa) would produce a
            // meaningless verdict, so report the lookup as unavailable rather than guessing.
            return [CreateMarketplaceUnavailableResult(metadata, $"The Marketplace response did not include a {channel} version.")];
        }

        var updateAvailable = SemVersion.ComparePrecedence(installedVersion, latestVersion) < 0;
        metadata["latestVersion"] = latestVersion.ToString();
        metadata["latestVersionChannel"] = channel;
        metadata["latestVersionKnown"] = true;
        metadata["updateAvailable"] = updateAvailable;

        if (!updateAvailable)
        {
            return [CreateInstalledResult(metadata, EnvironmentCheckStatus.Pass)];
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

    internal static VsCodeExtensionDetection Detect(IEnvironment environment, DirectoryInfo homeDirectory)
        => Detect(environment, homeDirectory, PathLookupHelper.FindFullPathFromPath);

    // The command resolver is injected so tests can exercise the PATH-based detection fallback
    // deterministically; PathLookupHelper.FindFullPathFromPath reads the real process PATH, which
    // cannot be mocked via IEnvironment and would otherwise leave that branch untested (and flaky
    // on machines that happen to have "code" on PATH).
    internal static VsCodeExtensionDetection Detect(
        IEnvironment environment,
        DirectoryInfo homeDirectory,
        Func<string, string?> commandResolver)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(commandResolver);

        if (!IsVsCodeInstalled(environment, commandResolver))
        {
            return new VsCodeExtensionDetection(VsCodeInstalled: false, ExtensionInstalled: false);
        }

        // The extension contributes its own version to every terminal, task, and debug process VS Code
        // creates for it, so this is the version of the instance that is actually running. It is
        // preferred over anything on disk: several extension roots can hold the extension at once
        // (desktop plus .vscode-server for Remote/WSL/devcontainers), --extensions-dir is invisible to
        // a child process, and portable mode relocates the whole root.
        //
        // The value is only trusted when it parses, so a truncated or corrupted variable falls through
        // to the disk scan instead of being reported as an unknown version.
        var reportedVersion = environment.GetEnvironmentVariable(ExtensionVersionEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(reportedVersion) &&
            SemVersion.TryParse(reportedVersion, SemVersionStyles.Strict, out _))
        {
            return new VsCodeExtensionDetection(
                VsCodeInstalled: true,
                ExtensionInstalled: true,
                ExtensionVersion: reportedVersion,
                VersionSource: VsCodeExtensionVersionSource.Extension);
        }

        // Outside a VS Code-created process there is no environment signal. Older extension builds also
        // predate the variable entirely, and those are exactly the installations this check exists to
        // find, so the manifest on disk has to be read rather than treating the missing variable as a
        // clean bill of health.
        var (installed, diskVersion, searchedRoots) = ResolveExtensionFromDisk(environment, homeDirectory);

        return new VsCodeExtensionDetection(
            VsCodeInstalled: true,
            ExtensionInstalled: installed,
            ExtensionVersion: diskVersion,
            VersionSource: diskVersion is null ? VsCodeExtensionVersionSource.None : VsCodeExtensionVersionSource.Manifest,
            SearchedRoots: searchedRoots);
    }

    /// <summary>
    /// Finds the highest Aspire extension version installed under any known extension root.
    /// </summary>
    /// <remarks>
    /// VS Code leaves the previous directory in place after an upgrade, so a root routinely holds
    /// several versions of the same extension at once. The highest version is the one VS Code loads,
    /// and versions are ordered by semver precedence rather than as strings so <c>1.10.0</c> sorts
    /// above <c>1.9.0</c>.
    /// </remarks>
    private static (bool Installed, string? Version, IReadOnlyList<string> SearchedRoots) ResolveExtensionFromDisk(
        IEnvironment environment,
        DirectoryInfo homeDirectory)
    {
        var searchedRoots = new List<string>();
        SemVersion? highestVersion = null;
        var installed = false;

        foreach (var extensionsDirectory in VsCodeInstallLayout.GetExtensionRootPaths(environment, homeDirectory))
        {
            searchedRoots.Add(extensionsDirectory);

            foreach (var extensionDirectory in EnumerateExtensionDirectories(extensionsDirectory))
            {
                installed = true;

                if (TryResolveExtensionVersion(extensionDirectory, out var version) &&
                    (highestVersion is null || SemVersion.ComparePrecedence(version, highestVersion) > 0))
                {
                    highestVersion = version;
                }
            }
        }

        return (installed, highestVersion?.ToString(), searchedRoots);
    }

    private static IEnumerable<string> EnumerateExtensionDirectories(string extensionsDirectory)
    {
        if (!Directory.Exists(extensionsDirectory))
        {
            yield break;
        }

        // IgnoreInaccessible lets the probe skip an unreadable extension folder and keep scanning the
        // rest, instead of throwing and reporting the whole extensions root as "not found" (a false
        // warning even when the Aspire extension is installed alongside an inaccessible one). The
        // parameterless EnumerateDirectories overload uses legacy behavior that throws instead.
        // AttributesToSkip is reset to None (the default EnumerationOptions skips Hidden/System) so an
        // extension folder is never silently ignored because of an unexpected attribute.
        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateDirectories(extensionsDirectory, "*", enumerationOptions).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable extensions root as empty rather than failing the whole doctor run.
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    // MoveNext performs the directory read, so enumeration faults surface here rather
                    // than from the call above. It cannot sit inside a try with a yield in scope, so
                    // the loop advances and yields in separate steps.
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                if (IsVersionedExtensionFolder(Path.GetFileName(current)))
                {
                    yield return current;
                }
            }
        }
    }

    /// <summary>
    /// Reads the version of an installed extension, preferring the manifest over the folder name.
    /// </summary>
    /// <remarks>
    /// The <c>&lt;publisher&gt;.&lt;name&gt;-&lt;version&gt;</c> folder name is a convention; the
    /// <c>version</c> field of the extracted <c>package.json</c> is the manifest contract, so it wins.
    /// The folder name is only consulted when the manifest is missing or unreadable, and then only a
    /// plain release version is accepted: a platform-specific VSIX unpacks to a folder such as
    /// <c>...-1.2.3-darwin-arm64</c>, whose suffix parses as the semver pre-release <c>1.2.3-darwin-arm64</c>
    /// and would otherwise be mistaken for a pre-release build of the extension.
    /// See https://code.visualstudio.com/api/working-with-extensions/publishing-extension#platformspecific-extensions.
    /// </remarks>
    private static bool TryResolveExtensionVersion(
        string extensionDirectory,
        [NotNullWhen(true)] out SemVersion? version)
    {
        if (TryReadManifestVersion(Path.Combine(extensionDirectory, "package.json"), out version))
        {
            return true;
        }

        var folderName = Path.GetFileName(extensionDirectory);
        var versionSegment = folderName[(ExtensionId.Length + 1)..];

        if (SemVersion.TryParse(versionSegment, SemVersionStyles.Strict, out var folderVersion) &&
            !folderVersion.IsPrerelease &&
            folderVersion.Metadata.Length == 0)
        {
            version = folderVersion;
            return true;
        }

        version = null;
        return false;
    }

    private static bool TryReadManifestVersion(string manifestPath, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;

        try
        {
            var manifest = new FileInfo(manifestPath);

            // An extension manifest is a few kilobytes. The cap stops doctor from reading an
            // arbitrarily large file that happens to sit at this path into memory.
            if (!manifest.Exists || manifest.Length > MaximumManifestSize)
            {
                return false;
            }

            using var stream = manifest.OpenRead();
            using var document = JsonDocument.Parse(stream);

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String &&
                SemVersion.TryParse(versionElement.GetString(), SemVersionStyles.Strict, out version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or locked manifest falls back to the folder name rather than failing the run.
            return false;
        }
    }

    private EnvironmentCheckResult CreateMarketplaceUnavailableResult(JsonObject metadata, Exception exception)
    {
        _logger.LogDebug(exception, "The VS Code Marketplace version check was unavailable.");

        return CreateMarketplaceUnavailableResult(metadata);
    }

    private EnvironmentCheckResult CreateMarketplaceUnavailableResult(JsonObject metadata, string reason)
    {
        _logger.LogDebug("The VS Code Marketplace version check was unavailable. {Reason}", reason);

        return CreateMarketplaceUnavailableResult(metadata);
    }

    private static EnvironmentCheckResult CreateMarketplaceUnavailableResult(JsonObject metadata)
    {
        metadata["latestVersionError"] = "unavailable";

        return CreateInstalledResult(
            metadata,
            EnvironmentCheckStatus.Warning,
            DoctorCommandStrings.VsCodeExtensionLatestVersionCheckUnavailableDetails);
    }

    private static EnvironmentCheckResult CreateInstalledResult(
        JsonObject metadata,
        EnvironmentCheckStatus status,
        string? details = null)
        => new()
        {
            Category = EnvironmentCheckCategories.DevelopmentTools,
            Name = CheckName,
            Status = status,
            Message = DoctorCommandStrings.VsCodeExtensionInstalledMessage,
            Details = details,
            Metadata = metadata
        };

    private static JsonObject BuildMetadata(
        VsCodeExtensionDetection detection,
        bool updateCheckEnabled)
    {
        var metadata = new JsonObject
        {
            ["vsCodeInstalled"] = detection.VsCodeInstalled,
            ["extensionInstalled"] = detection.ExtensionInstalled,
            ["extensionId"] = ExtensionId
        };

        if (!detection.ExtensionInstalled)
        {
            return metadata;
        }

        metadata["updateCheckEnabled"] = updateCheckEnabled;
        metadata["latestVersionKnown"] = false;
        if (detection.ExtensionVersion is not null)
        {
            metadata["extensionVersion"] = detection.ExtensionVersion;
        }

        metadata["extensionVersionSource"] = detection.VersionSource switch
        {
            VsCodeExtensionVersionSource.Extension => "extension",
            VsCodeExtensionVersionSource.Manifest => "manifest",
            _ => "unknown"
        };

        return metadata;
    }

    /// <summary>
    /// Renders the extension roots that were searched so an unknown version says where it looked.
    /// </summary>
    private static string FormatSearchedRoots(IReadOnlyList<string>? searchedRoots)
    {
        // The environment variable path never touches disk, so there is nothing to list. That happens
        // only when the variable itself was unreadable, which the fix text already covers.
        if (searchedRoots is null || searchedRoots.Count == 0)
        {
            return DoctorCommandStrings.VsCodeExtensionVersionUnknownDetails;
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            DoctorCommandStrings.VsCodeExtensionVersionUnknownSearchedDetailsFormat,
            string.Join(", ", searchedRoots));
    }

    private static bool IsVsCodeInstalled(
        IEnvironment environment,
        Func<string, string?> commandResolver)
    {
        // VS Code sets TERM_PROGRAM for integrated terminals. Outside an integrated terminal,
        // probe the stable and Insiders launchers without spawning either process.
        // See https://code.visualstudio.com/docs/terminal/shell-integration.
        return string.Equals(
                environment.GetEnvironmentVariable("TERM_PROGRAM"),
                "vscode",
                StringComparison.OrdinalIgnoreCase)
            || commandResolver("code") is not null
            || commandResolver("code-insiders") is not null;
    }

    // Matches an extension folder name against the Aspire extension id. A case-insensitive prefix match
    // tolerates any installed version without spawning the VS Code CLI. Requiring a digit immediately
    // after the trailing '-' pins the match to the version segment so a different extension whose id
    // starts with ours (e.g. "microsoft-aspire.aspire-vscode-extras-1.0.0") is not treated as a match.
    private static bool IsVersionedExtensionFolder(string folderName)
    {
        const string prefix = ExtensionId + "-";

        return folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            folderName.Length > prefix.Length &&
            char.IsAsciiDigit(folderName[prefix.Length]);
    }
}

/// <summary>
/// Identifies where an installed extension version was read from.
/// </summary>
internal enum VsCodeExtensionVersionSource
{
    /// <summary>
    /// No version could be determined.
    /// </summary>
    None,

    /// <summary>
    /// The running extension reported its own version through the environment.
    /// </summary>
    Extension,

    /// <summary>
    /// The version was read from an installed extension's manifest or folder name on disk.
    /// </summary>
    Manifest
}

/// <summary>
/// Captures whether VS Code and the Aspire VS Code extension were detected, the version that was
/// resolved for the extension, and where that version came from.
/// </summary>
/// <param name="VsCodeInstalled">Whether a VS Code build was detected.</param>
/// <param name="ExtensionInstalled">Whether the Aspire extension was detected.</param>
/// <param name="ExtensionVersion">The resolved extension version, or <see langword="null"/> when it could not be determined.</param>
/// <param name="VersionSource">Where <paramref name="ExtensionVersion"/> was read from.</param>
/// <param name="SearchedRoots">
/// The extension roots the disk scan looked at, used to explain an unknown version. It is
/// <see langword="null"/> when the version came from the environment and no scan ran.
/// </param>
internal sealed record VsCodeExtensionDetection(
    bool VsCodeInstalled,
    bool ExtensionInstalled,
    string? ExtensionVersion = null,
    VsCodeExtensionVersionSource VersionSource = VsCodeExtensionVersionSource.None,
    IReadOnlyList<string>? SearchedRoots = null);
