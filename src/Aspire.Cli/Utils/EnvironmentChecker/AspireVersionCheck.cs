// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Reports the installed Aspire CLI version, whether a newer CLI version is available, and
/// the Aspire SDK version used by the selected AppHost when one can be discovered.
/// </summary>
internal sealed class AspireVersionCheck(
    ICliUpdateNotifier updateNotifier,
    IProjectLocator projectLocator,
    IAppHostProjectFactory projectFactory,
    IIdentityChannelReader identityChannelReader,
    CliExecutionContext executionContext,
    ILogger<AspireVersionCheck> logger) : IEnvironmentCheck
{
    // Version checks should appear first so users immediately see which Aspire bits
    // produced the rest of the doctor output before reading environment diagnostics.
    public int Order => 0;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>
        {
            await GetCliVersionCheckAsync(cancellationToken)
        };

        EnvironmentCheckResult? appHostVersionCheck;
        try
        {
            appHostVersionCheck = await GetAppHostVersionCheckAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve AppHost version.");

            appHostVersionCheck = new EnvironmentCheckResult
            {
                Category = "apphost",
                Name = "apphost-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                Details = ex.Message
            };
        }

        if (appHostVersionCheck is not null)
        {
            results.Add(appHostVersionCheck);
        }

        return results;
    }

    private async Task<EnvironmentCheckResult> GetCliVersionCheckAsync(CancellationToken cancellationToken)
    {
        // Read the identity channel up front so it can be attached to every
        // CLI-version result (pass / out-of-date / update-check-failed).
        // Identity channel is best-effort: misconfigured dev builds may have
        // missing metadata, and we don't want to fail doctor over a labelling
        // gap. ReadChannel() throws InvalidOperationException in that case;
        // logging at debug keeps the diagnostic available without surfacing
        // a scary warning in the human-readable output.
        var identityChannel = TryReadIdentityChannel();

        try
        {
            var status = await updateNotifier.GetVersionStatusAsync(executionContext.WorkingDirectory, cancellationToken);
            var currentVersion = string.IsNullOrWhiteSpace(status.CurrentVersion) ? DoctorCommandStrings.VersionUnknown : status.CurrentVersion;

            // Doctor should always report the installed CLI version. Treat update lookup
            // failures as a warning on that same check rather than hiding the version or
            // failing the command because offline/private-feed scenarios are common.
            if (status.UpdateCheckError is { Length: > 0 } updateCheckError)
            {
                return new EnvironmentCheckResult
                {
                    Category = "aspire",
                    Name = "cli-version",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = FormatCliVersionMessage(currentVersion, identityChannel),
                    Details = $"{DoctorCommandStrings.CliVersionUpdateCheckFailedMessage}: {updateCheckError}",
                    Metadata = BuildCliVersionMetadata(currentVersion, latestVersion: null, status.UpdateCommand, updateCheckError, identityChannel, latestVersionChannel: null)
                };
            }

            if (status.LatestVersion is { Length: > 0 } latestVersion)
            {
                return new EnvironmentCheckResult
                {
                    Category = "aspire",
                    Name = "cli-version",
                    Status = EnvironmentCheckStatus.Warning,
                    // Both versions get their channel inline next to them so
                    // the message is unambiguous:
                    //   "...version 13.4.0-dev (channel: local) is out of
                    //    date. Latest version is X (channel: prerelease)"
                    // The current-version channel comes from the running
                    // CLI's baked AspireCliChannel; the latest-version
                    // channel comes from the update notifier's recommendation
                    // lane (stable vs prerelease).
                    Message = string.Format(
                        CultureInfo.CurrentCulture,
                        DoctorCommandStrings.CliVersionOutOfDateMessageFormat,
                        WithChannelSuffix(currentVersion, identityChannel),
                        WithChannelSuffix(latestVersion, status.LatestVersionChannel)),
                    Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliVersionOutOfDateFixFormat, status.UpdateCommand ?? "aspire update"),
                    Metadata = BuildCliVersionMetadata(currentVersion, latestVersion, status.UpdateCommand, updateCheckError: null, identityChannel, status.LatestVersionChannel)
                };
            }

            return new EnvironmentCheckResult
            {
                Category = "aspire",
                Name = "cli-version",
                Status = EnvironmentCheckStatus.Pass,
                Message = FormatCliVersionMessage(currentVersion, identityChannel),
                Metadata = BuildCliVersionMetadata(currentVersion, latestVersion: null, status.UpdateCommand, updateCheckError: null, identityChannel, latestVersionChannel: null)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check Aspire CLI version.");

            return new EnvironmentCheckResult
            {
                Category = "aspire",
                Name = "cli-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.CliVersionUpdateCheckFailedMessage,
                Details = ex.Message,
                Metadata = BuildCliVersionMetadata(currentVersion: null, latestVersion: null, updateCommand: null, updateCheckError: ex.Message, identityChannel, latestVersionChannel: null)
            };
        }
    }

    private static string FormatCliVersionMessage(string currentVersion, string? identityChannel)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            DoctorCommandStrings.CliVersionMessageFormat,
            WithChannelSuffix(currentVersion, identityChannel));
    }

    /// <summary>
    /// Returns <paramref name="version"/> with the channel suffix appended
    /// inline (e.g. <c>"13.0.0 (channel: stable)"</c>) so the channel is
    /// unambiguously attached to that specific version in any message
    /// template that mentions multiple versions.
    /// </summary>
    private static string WithChannelSuffix(string version, string? channel)
    {
        if (string.IsNullOrEmpty(channel))
        {
            return version;
        }

        return version + string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ChannelSuffixFormat, channel);
    }

    /// <summary>
    /// Appends the channel suffix to an arbitrary message. Used only for
    /// message templates that mention exactly one version (so there's no
    /// ambiguity about which version the channel qualifies). For templates
    /// with multiple versions, use <see cref="WithChannelSuffix"/> inline
    /// on the relevant version slot instead.
    /// </summary>
    private static string AppendChannelSuffix(string message, string? channel)
    {
        if (string.IsNullOrEmpty(channel))
        {
            return message;
        }

        return message + string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ChannelSuffixFormat, channel);
    }

    private string? TryReadIdentityChannel()
    {
        try
        {
            return identityChannelReader.ReadChannel();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Identity channel is informational; a misconfigured dev build
            // (no AspireCliChannel assembly metadata) must not break doctor.
            logger.LogDebug(ex, "Could not read identity channel for doctor output.");
            return null;
        }
    }

    private async Task<EnvironmentCheckResult?> GetAppHostVersionCheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FileInfo> appHostFiles;
        try
        {
            appHostFiles = await ResolveAppHostFilesAsync(cancellationToken);
        }
        catch (ProjectLocatorException ex) when (ex.FailureReason is ProjectLocatorFailureReason.NoProjectFileFound or ProjectLocatorFailureReason.ProjectFileDoesntExist)
        {
            // Doctor is useful outside an Aspire app too; no AppHost simply means there is
            // no AppHost version check to include.
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProjectLocatorException ex)
        {
            return new EnvironmentCheckResult
            {
                Category = "apphost",
                Name = "apphost-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                Details = ex.Message
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to find Aspire AppHost for version check.");

            return new EnvironmentCheckResult
            {
                Category = "apphost",
                Name = "apphost-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                Details = ex.Message
            };
        }

        // Pinned channel is best-effort and informational: AppHost discovery already
        // succeeded, so an unreadable / malformed aspire.config.json must not flip
        // doctor into a failure state. A null pinnedChannel simply omits the field
        // from the JSON metadata and the channel suffix from the human-readable
        // message — same behavior as a project that has not pinned a channel.
        // Hoisted above the loop because the source directory is loop-invariant;
        // re-reading per-AppHost would do redundant I/O and duplicate log lines
        // on a misconfigured file.
        var pinnedChannel = TryReadPinnedChannel(executionContext.WorkingDirectory);

        foreach (var appHostFile in appHostFiles)
        {
            var relativePath = GetRelativePath(appHostFile);

            try
            {
                var (isAppHost, version) = await ResolveAppHostVersionAsync(appHostFile, cancellationToken);
                if (!isAppHost)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    return new EnvironmentCheckResult
                    {
                        Category = "apphost",
                        Name = "apphost-version",
                        Status = EnvironmentCheckStatus.Warning,
                        Message = AppendChannelSuffix(
                            string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.AppHostVersionUnknownMessageFormat, relativePath),
                            pinnedChannel),
                        Metadata = BuildAppHostVersionMetadata(relativePath, version: null, pinnedChannel)
                    };
                }

                return new EnvironmentCheckResult
                {
                    Category = "apphost",
                    Name = "apphost-version",
                    Status = EnvironmentCheckStatus.Pass,
                    // Channel goes inline next to the version, not at the
                    // end of the message: "AppHost version 13.0.0 (channel: stable) (path/to/AppHost.csproj)"
                    // rather than "AppHost version 13.0.0 (path/to/AppHost.csproj) (channel: stable)"
                    // — the format trails the version with the AppHost
                    // path, so a tail-appended channel would attach to the
                    // path instead.
                    Message = string.Format(
                        CultureInfo.CurrentCulture,
                        DoctorCommandStrings.AppHostVersionMessageFormat,
                        WithChannelSuffix(version, pinnedChannel),
                        relativePath),
                    Metadata = BuildAppHostVersionMetadata(relativePath, version, pinnedChannel)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to check Aspire AppHost version for {AppHostPath}.", appHostFile.FullName);

                return new EnvironmentCheckResult
                {
                    Category = "apphost",
                    Name = "apphost-version",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                    Details = ex.Message,
                    Metadata = BuildAppHostVersionMetadata(relativePath, version: null, pinnedChannel)
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the pinned channel from <c>aspire.config.json</c> sitting in
    /// <paramref name="configDirectory"/> (the CLI's current working directory —
    /// the same anchor used by AppHost discovery). Returns <see langword="null"/>
    /// when the file is absent, malformed, or has no <c>channel</c> field. The
    /// lookup is best effort and never throws — doctor uses this only to enrich the
    /// AppHost-version line.
    /// </summary>
    private string? TryReadPinnedChannel(DirectoryInfo configDirectory)
    {
        var directory = configDirectory.FullName;
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            var config = AspireConfigFile.Load(directory);
            var channel = config?.Channel;
            return string.IsNullOrWhiteSpace(channel) ? null : channel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read pinned channel from aspire.config.json in {Directory}.", directory);
            return null;
        }
    }

    private async Task<IReadOnlyList<FileInfo>> ResolveAppHostFilesAsync(CancellationToken cancellationToken)
    {
        // AppHost version reporting is intentionally shallow: use an AppHost explicitly
        // configured in the current directory, or exactly one AppHost-looking file in
        // the current directory. Avoid recursive discovery so doctor does not choose
        // between multiple AppHosts or pay the cost of project-wide AppHost search for
        // an informational version check.
        var configuredAppHost = await projectLocator.GetAppHostFromSettingsAsync(executionContext.WorkingDirectory, searchParentDirectories: false, cancellationToken);
        if (configuredAppHost is not null)
        {
            return [configuredAppHost];
        }

        var candidates = await projectLocator.FindAppHostProjectFilesAsync(
            executionContext.WorkingDirectory,
            AppHostDiscoveryScope.ExplicitDirectory,
            maxDepth: 0,
            cancellationToken);

        if (candidates.Count > 1)
        {
            logger.LogDebug(
                "Found multiple AppHost candidates in {WorkingDirectory}; skipping AppHost version check because no AppHost is configured.",
                executionContext.WorkingDirectory.FullName);
            return [];
        }

        return candidates;
    }

    private async Task<(bool IsAppHost, string? Version)> ResolveAppHostVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        var project = projectFactory.TryGetProject(appHostFile);
        if (project is null)
        {
            return (false, null);
        }

        var validationResult = await project.ValidateAppHostAsync(appHostFile, cancellationToken);
        if (validationResult.IsValid)
        {
            return (true, validationResult.AspireHostingVersion ?? await project.GetAspireHostingVersionAsync(appHostFile, cancellationToken));
        }

        // A project named like an AppHost may fail validation because it is not currently
        // buildable. Keep reporting the candidate so doctor can explain that the version is unknown,
        // but suppress ordinary projects that only matched the broad language detection patterns.
        return (validationResult.IsPossiblyUnbuildable, null);
    }

    private string GetRelativePath(FileInfo file)
    {
        return Path.GetRelativePath(executionContext.WorkingDirectory.FullName, file.FullName);
    }

    private static JsonObject BuildCliVersionMetadata(string? currentVersion, string? latestVersion, string? updateCommand, string? updateCheckError, string? identityChannel, string? latestVersionChannel)
    {
        var metadata = new JsonObject();

        if (!string.IsNullOrWhiteSpace(currentVersion))
        {
            metadata["currentVersion"] = currentVersion;
        }

        if (!string.IsNullOrWhiteSpace(latestVersion))
        {
            metadata["latestVersion"] = latestVersion;
        }

        if (!string.IsNullOrWhiteSpace(updateCommand))
        {
            metadata["updateCommand"] = updateCommand;
        }

        if (!string.IsNullOrWhiteSpace(updateCheckError))
        {
            metadata["updateCheckError"] = updateCheckError;
        }

        if (!string.IsNullOrWhiteSpace(identityChannel))
        {
            metadata["identityChannel"] = identityChannel;
        }

        if (!string.IsNullOrWhiteSpace(latestVersionChannel))
        {
            metadata["latestVersionChannel"] = latestVersionChannel;
        }

        return metadata;
    }

    private static JsonObject BuildAppHostVersionMetadata(string relativePath, string? version, string? pinnedChannel)
    {
        var metadata = new JsonObject
        {
            ["appHostPath"] = relativePath
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            metadata["version"] = version;
        }

        if (!string.IsNullOrWhiteSpace(pinnedChannel))
        {
            metadata["pinnedChannel"] = pinnedChannel;
        }

        return metadata;
    }
}
