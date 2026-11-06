// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if a supported container runtime is available and running.
/// </summary>
internal sealed class ContainerRuntimeCheck(ILogger<ContainerRuntimeCheck> logger, IEnvironment environment) : IEnvironmentCheck
{
    internal const string CheckName = "container-runtime";

    /// <summary>
    /// Minimum Docker version required for Aspire.
    /// </summary>
    public const string MinimumDockerVersion = "28.0.0";

    /// <summary>
    /// Minimum Podman version required for Aspire.
    /// </summary>
    public const string MinimumPodmanVersion = "5.0.0";

    // Matches DCP's minimum supported WSLc client and session manager version.
    public const string MinimumWslcVersion = "3.0.2.0";

    public int Order => 40; // Process check - more expensive

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var configuredRuntime = environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME")
                ?? environment.GetEnvironmentVariable("DOTNET_ASPIRE_CONTAINER_RUNTIME");
            configuredRuntime = string.IsNullOrEmpty(configuredRuntime) ? null : configuredRuntime;

            if (!environment.IsWindows() &&
                string.Equals(configuredRuntime, KnownContainerRuntimes.Wslc, StringComparisons.ContainerRuntime))
            {
                return [new EnvironmentCheckResult
                {
                    Category = EnvironmentCheckCategories.Container,
                    Name = KnownContainerRuntimes.Wslc,
                    Status = EnvironmentCheckStatus.Fail,
                    Message = DoctorCommandStrings.ContainerRuntimeWslcUnsupportedHostMessage + DoctorCommandStrings.ContainerRuntimeExplicitSelectionSuffix,
                    Fix = DoctorCommandStrings.ContainerRuntimeSelectAlternativeFix,
                    Link = "https://aka.ms/aspire/containers"
                }];
            }

            if (configuredRuntime is not null)
            {
                var runtime = await ContainerRuntimeDetector.FindAvailableRuntimeAsync(configuredRuntime, logger, cancellationToken)
                    ?? throw new InvalidOperationException($"No detection result was returned for configured container runtime '{configuredRuntime}'.");

                return [BuildRuntimeResult(runtime, isSelected: true, hasHealthyRuntime: false, configuredRuntime, cancellationToken)];
            }

            var dockerTask = ContainerRuntimeDetector.CheckRuntimeAsync(KnownContainerRuntimes.Docker, logger, cancellationToken);
            var podmanTask = ContainerRuntimeDetector.CheckRuntimeAsync(KnownContainerRuntimes.Podman, logger, cancellationToken);
            var runtimes = environment.IsWindows()
                ? await Task.WhenAll(dockerTask, podmanTask, ContainerRuntimeDetector.CheckRuntimeAsync(KnownContainerRuntimes.Wslc, logger, cancellationToken))
                : await Task.WhenAll(dockerTask, podmanTask);

            // Select best from already-probed results (no re-probing)
            var healthyRuntime = ContainerRuntimeDetector.FindBestRuntime(
                runtimes.Where(info => info.IsHealthy && CheckRuntimePolicy(info)?.Status != EnvironmentCheckStatus.Fail));
            var selected = ContainerRuntimeDetector.FindBestRuntime(runtimes);
            var results = new List<EnvironmentCheckResult>();
            foreach (var info in runtimes.OrderBy(info => info.Priority))
            {
                if (!info.IsInstalled)
                {
                    continue;
                }

                var isSelected = selected is not null &&
                    string.Equals(info.Key, selected.Key, StringComparisons.ContainerRuntime);

                results.Add(BuildRuntimeResult(info, isSelected, healthyRuntime is not null, configuredRuntime, cancellationToken));
            }

            if (results.Count > 0)
            {
                return results;
            }

            return [new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Fail,
                Message = DoctorCommandStrings.ContainerRuntimeNotDetectedMessage,
                Fix = environment.IsWindows()
                    ? DoctorCommandStrings.ContainerRuntimeInstallWindowsFix
                    : DoctorCommandStrings.ContainerRuntimeInstallFix,
                Link = "https://aka.ms/aspire/containers"
            }];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking container runtime");
            return [new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Fail,
                Message = DoctorCommandStrings.ContainerRuntimeCheckFailedMessage,
                Details = ex.Message
            }];
        }
    }

    /// <summary>
    /// Applies Aspire-specific policy checks (minimum version, Windows containers)
    /// using version info already gathered by the detector. No process spawning.
    /// </summary>
    internal static EnvironmentCheckResult? CheckRuntimePolicy(ContainerRuntimeInfo info)
    {
        if (info.VersionError is not null)
        {
            return new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = info.Key,
                Status = EnvironmentCheckStatus.Fail,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeVersionUnavailableMessageFormat, info.Name),
                Details = info.VersionError,
                Fix = GetContainerRuntimeUpgradeAdvice(info.Key, info.Name),
                Link = "https://aka.ms/aspire/containers"
            };
        }

        // Blocking compatibility failures take precedence over advisory version warnings.
        if (string.Equals(info.Key, KnownContainerRuntimes.Docker, StringComparisons.ContainerRuntime) &&
            string.Equals(info.ServerOs, "windows", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeName = info.IsDockerDesktop ? "Docker Desktop" : info.Name;
            return new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Fail,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeWindowsModeMessageFormat, runtimeName),
                Details = DoctorCommandStrings.ContainerRuntimeWindowsModeDetails,
                Fix = DoctorCommandStrings.ContainerRuntimeWindowsModeFix,
                Link = "https://aka.ms/aspire/containers"
            };
        }

        var minimumVersion = GetMinimumVersion(info.Key);

        // Check minimum client version
        if (info.ClientVersion is not null && minimumVersion is not null &&
            (info.ClientVersion < minimumVersion || IsUnsupportedPrerelease(info.ClientVersion, info.IsClientPrerelease, minimumVersion)))
        {
            return VersionPolicyResult(info.Key,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeClientVersionWarningFormat,
                    info.Name, info.ClientVersionText ?? info.ClientVersion.ToString(), GetMinimumVersionString(info.Key)),
                GetContainerRuntimeUpgradeAdvice(info.Key, info.Name));
        }

        // Podman does not always expose a separate server version.
        if (info.Key is KnownContainerRuntimes.Docker or KnownContainerRuntimes.Wslc && info.ServerVersion is not null && minimumVersion is not null &&
            (info.ServerVersion < minimumVersion || IsUnsupportedPrerelease(info.ServerVersion, info.IsServerPrerelease, minimumVersion)))
        {
            return VersionPolicyResult(info.Key,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeServerVersionWarningFormat,
                    info.Name, info.ServerVersionText ?? info.ServerVersion.ToString(), GetMinimumVersionString(info.Key)),
                GetContainerRuntimeUpgradeAdvice(info.Key, info.Name));
        }

        return null; // No issues
    }

    private static bool IsUnsupportedPrerelease(Version version, bool isPrerelease, Version minimumVersion)
    {
        return isPrerelease &&
            new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0)) <=
            new Version(minimumVersion.Major, minimumVersion.Minor, Math.Max(minimumVersion.Build, 0), Math.Max(minimumVersion.Revision, 0));
    }

    private static EnvironmentCheckResult VersionPolicyResult(string runtimeKey, string message, string fix) => new()
    {
        Category = EnvironmentCheckCategories.Container,
        Name = CheckName,
        Status = string.Equals(runtimeKey, KnownContainerRuntimes.Wslc, StringComparisons.ContainerRuntime)
            ? EnvironmentCheckStatus.Fail
            : EnvironmentCheckStatus.Warning,
        Message = message,
        Fix = fix,
        Link = "https://aka.ms/aspire/containers"
    };

    private static EnvironmentCheckResult BuildRuntimeResult(
        ContainerRuntimeInfo info,
        bool isSelected,
        bool hasHealthyRuntime,
        string? configuredRuntime,
        CancellationToken _)
    {
        var selectedSuffix = isSelected
            ? configuredRuntime is not null ? DoctorCommandStrings.ContainerRuntimeExplicitSelectionSuffix : DoctorCommandStrings.ContainerRuntimeActiveSuffix
            : "";

        if (!info.IsInstalled)
        {
            // Only reached for explicitly configured runtimes
            return new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = info.Key,
                Status = EnvironmentCheckStatus.Fail,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeNotFoundMessageFormat,
                    info.Name, configuredRuntime, selectedSuffix),
                Fix = GetContainerRuntimeInstallationLink(info.Key, info.Name)
            };
        }

        if (!info.IsRunning)
        {
            return new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Container,
                Name = info.Key,
                Status = EnvironmentCheckStatus.Warning,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeStoppedMessageFormat,
                    info.Name, selectedSuffix),
                Details = info.Error,
                Fix = GetContainerRuntimeStartupAdvice(info.Key, info.Name, info.IsDockerDesktop)
            };
        }

        // Runtime is healthy — apply Aspire-specific policy checks (no process spawning)
        var policyResult = CheckRuntimePolicy(info);
        if (policyResult is not null)
        {
            // Append selection info to the policy result message
            return new EnvironmentCheckResult
            {
                Category = policyResult.Category,
                Name = policyResult.Name,
                Status = !isSelected && hasHealthyRuntime && policyResult.Status == EnvironmentCheckStatus.Fail
                    ? EnvironmentCheckStatus.Warning
                    : policyResult.Status,
                Message = policyResult.Message + selectedSuffix,
                Fix = policyResult.Fix,
                Details = policyResult.Details,
                Link = policyResult.Link
            };
        }

        var versionSuffix = info.ClientVersion is not null
            ? string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeVersionSuffixFormat, info.ClientVersionText ?? info.ClientVersion.ToString())
            : "";

        return new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Container,
            Name = info.Key,
            Status = EnvironmentCheckStatus.Pass,
            Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeRunningMessageFormat,
                info.Name, versionSuffix, selectedSuffix)
        };
    }

    private static string GetContainerRuntimeInstallationLink(string runtimeKey, string runtimeName)
    {
        return runtimeKey switch
        {
            KnownContainerRuntimes.Docker => DoctorCommandStrings.ContainerRuntimeInstallDockerFix,
            KnownContainerRuntimes.Podman => DoctorCommandStrings.ContainerRuntimeInstallPodmanFix,
            KnownContainerRuntimes.Wslc => DoctorCommandStrings.ContainerRuntimeInstallWslcFix,
            _ => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeInstallGenericFixFormat, runtimeName)
        };
    }

    /// <summary>
    /// Gets the minimum required version for the specified container runtime.
    /// </summary>
    private static Version? GetMinimumVersion(string runtime)
    {
        var versionString = GetMinimumVersionString(runtime);

        if (versionString is not null && Version.TryParse(versionString, out var version))
        {
            if (string.Equals(runtime, KnownContainerRuntimes.Wslc, StringComparisons.ContainerRuntime) && version.Revision == 0)
            {
                // WSLc reports clients as 3.0.2.0 and session managers as 3.0.2.
                // Omit a zero revision from the minimum so either form compares correctly.
                version = new Version(version.Major, version.Minor, version.Build);
            }

            return version;
        }

        return null;
    }

    /// <summary>
    /// Gets the minimum required version string for the specified container runtime.
    /// </summary>
    private static string? GetMinimumVersionString(string runtime)
    {
        return runtime switch
        {
            KnownContainerRuntimes.Docker => MinimumDockerVersion,
            KnownContainerRuntimes.Podman => MinimumPodmanVersion,
            KnownContainerRuntimes.Wslc => MinimumWslcVersion,
            _ => null
        };
    }

    private static string GetContainerRuntimeUpgradeAdvice(string runtimeKey, string runtimeName)
    {
        return runtimeKey switch
        {
            KnownContainerRuntimes.Docker => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeUpgradeFixFormat,
                runtimeName, MinimumDockerVersion, "https://www.docker.com/products/docker-desktop"),
            KnownContainerRuntimes.Podman => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeUpgradeFixFormat,
                runtimeName, MinimumPodmanVersion, "https://podman.io/getting-started/installation"),
            KnownContainerRuntimes.Wslc => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeUpgradeWslcFixFormat, MinimumWslcVersion),
            _ => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeUpgradeGenericFixFormat, runtimeName)
        };
    }

    private static string GetContainerRuntimeStartupAdvice(string runtimeKey, string runtimeName, bool isDockerDesktop)
    {
        return runtimeKey switch
        {
            KnownContainerRuntimes.Docker when isDockerDesktop => DoctorCommandStrings.ContainerRuntimeStartDockerDesktopFix,
            KnownContainerRuntimes.Podman => DoctorCommandStrings.ContainerRuntimeStartPodmanFix,
            KnownContainerRuntimes.Wslc => DoctorCommandStrings.ContainerRuntimeStartWslcFix,
            _ => string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.ContainerRuntimeStartDaemonFixFormat, runtimeName)
        };
    }
}
