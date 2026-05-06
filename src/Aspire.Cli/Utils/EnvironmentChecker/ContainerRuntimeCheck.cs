// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if a container runtime (Docker or Podman) is available and running.
/// </summary>
internal sealed class ContainerRuntimeCheck(ILogger<ContainerRuntimeCheck> logger) : IEnvironmentCheck
{

    /// <summary>
    /// Minimum Docker version required for Aspire.
    /// </summary>
    public const string MinimumDockerVersion = "28.0.0";

    /// <summary>
    /// Minimum Podman version required for Aspire.
    /// </summary>
    public const string MinimumPodmanVersion = "5.0.0";

    public int Order => 40; // Process check - more expensive

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Probe all runtimes in parallel
            var dockerTask = ContainerRuntimeDetector.CheckRuntimeAsync("docker", "Docker", isDefault: true, logger, cancellationToken);
            var podmanTask = ContainerRuntimeDetector.CheckRuntimeAsync("podman", "Podman", isDefault: false, logger, cancellationToken);
            var runtimes = await Task.WhenAll(dockerTask, podmanTask);

            var configuredRuntime = Environment.GetEnvironmentVariable(KnownConfigNames.ContainerRuntime)
                ?? Environment.GetEnvironmentVariable(KnownConfigNames.Legacy.ContainerRuntime);

            // Select best from already-probed results (no re-probing)
            ContainerRuntimeInfo? selected;
            if (configuredRuntime is not null)
            {
                selected = runtimes.FirstOrDefault(r =>
                    string.Equals(r.Executable, configuredRuntime, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                selected = ContainerRuntimeDetector.FindBestRuntime(runtimes);
            }

            var results = new List<EnvironmentCheckResult>();

            // Only report runtimes that are installed (or explicitly configured)
            foreach (var info in runtimes)
            {
                if (!info.IsInstalled && (configuredRuntime is null ||
                    !string.Equals(info.Executable, configuredRuntime, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var isSelected = selected is not null &&
                    string.Equals(info.Executable, selected.Executable, StringComparison.OrdinalIgnoreCase);

                results.Add(BuildRuntimeResult(info, isSelected, configuredRuntime, cancellationToken));
            }

            // If nothing is available, show a single failure
            if (results.Count == 0)
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = "container",
                    Name = "container-runtime",
                    Status = EnvironmentCheckStatus.Fail,
                    Message = "No container runtime detected",
                    Fix = "Install Docker Desktop: https://www.docker.com/products/docker-desktop or Podman: https://podman.io/getting-started/installation",
                    Link = "https://aka.ms/aspire/containers"
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking container runtime");
            return [new EnvironmentCheckResult
            {
                Category = "container",
                Name = "container-runtime",
                Status = EnvironmentCheckStatus.Fail,
                Message = "Failed to check container runtime",
                Details = ex.Message
            }];
        }
    }

    /// <summary>
    /// Applies Aspire-specific policy checks (minimum version, Windows containers)
    /// using version info already gathered by the detector. No process spawning.
    /// </summary>
    private static EnvironmentCheckResult? CheckRuntimePolicy(ContainerRuntimeInfo info)
    {
        var minimumVersion = GetMinimumVersion(info.Name);

        // Check minimum client version
        if (info.ClientVersion is not null && minimumVersion is not null && info.ClientVersion < minimumVersion)
        {
            return WarningResult(
                $"{info.Name} client version {info.ClientVersion} is below minimum required {GetMinimumVersionString(info.Name)}",
                GetContainerRuntimeUpgradeAdvice(info.Name));
        }

        // Check minimum server version (Docker only)
        if (info.Name == "Docker" && info.ServerVersion is not null && minimumVersion is not null && info.ServerVersion < minimumVersion)
        {
            return WarningResult(
                $"{info.Name} server version {info.ServerVersion} is below minimum required {GetMinimumVersionString(info.Name)}",
                GetContainerRuntimeUpgradeAdvice(info.Name));
        }

        // Docker-specific: check Windows container mode
        if (info.Name == "Docker" && string.Equals(info.ServerOs, "windows", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeName = info.IsDockerDesktop ? "Docker Desktop" : "Docker";
            return new EnvironmentCheckResult
            {
                Category = "container",
                Name = "container-runtime",
                Status = EnvironmentCheckStatus.Fail,
                Message = $"{runtimeName} is running in Windows container mode",
                Details = "Aspire requires Linux containers. Windows containers are not supported.",
                Fix = "Switch Docker Desktop to Linux containers mode (right-click Docker tray icon → 'Switch to Linux containers...')",
                Link = "https://aka.ms/aspire/containers"
            };
        }

        return null; // No issues
    }

    private static EnvironmentCheckResult WarningResult(string message, string fix) => new()
    {
        Category = "container",
        Name = "container-runtime",
        Status = EnvironmentCheckStatus.Warning,
        Message = message,
        Fix = fix,
        Link = "https://aka.ms/aspire/containers"
    };

    private static EnvironmentCheckResult BuildRuntimeResult(
        ContainerRuntimeInfo info,
        bool isSelected,
        string? configuredRuntime,
        CancellationToken _)
    {
        var selectedSuffix = isSelected ? " ← active" : "";

        if (!info.IsInstalled)
        {
            // Only reached for explicitly configured runtimes
            return new EnvironmentCheckResult
            {
                Category = "container",
                Name = info.Executable,
                Status = EnvironmentCheckStatus.Fail,
                Message = $"{info.Name}: not found (configured via ASPIRE_CONTAINER_RUNTIME={configuredRuntime})",
                Fix = GetContainerRuntimeInstallationLink(info.Name)
            };
        }

        if (!info.IsRunning)
        {
            return new EnvironmentCheckResult
            {
                Category = "container",
                Name = info.Executable,
                Status = EnvironmentCheckStatus.Warning,
                Message = $"{info.Name}: installed but not running{selectedSuffix}",
                Fix = GetContainerRuntimeStartupAdvice(info.Name, info.IsDockerDesktop)
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
                Status = policyResult.Status,
                Message = policyResult.Message + selectedSuffix,
                Fix = policyResult.Fix,
                Details = policyResult.Details,
                Link = policyResult.Link
            };
        }

        // Explain why this runtime was chosen
        var reason = configuredRuntime is not null && isSelected
            ? $"configured via ASPIRE_CONTAINER_RUNTIME={configuredRuntime}"
            : isSelected && info.IsDefault ? "auto-detected (default)"
            : isSelected ? "auto-detected (only runtime running)"
            : "available";

        var versionSuffix = info.ClientVersion is not null ? $" v{info.ClientVersion}" : "";

        return new EnvironmentCheckResult
        {
            Category = "container",
            Name = info.Executable,
            Status = EnvironmentCheckStatus.Pass,
            Message = $"{info.Name}{versionSuffix}: running ({reason}){selectedSuffix}"
        };
    }

    private static string GetContainerRuntimeInstallationLink(string runtime)
    {
        return runtime switch
        {
            "Docker" => "Install Docker Desktop: https://www.docker.com/products/docker-desktop",
            "Podman" => "Install Podman: https://podman.io/getting-started/installation",
            _ => $"Install {runtime}"
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
            "Docker" => MinimumDockerVersion,
            "Podman" => MinimumPodmanVersion,
            _ => null
        };
    }

    private static string GetContainerRuntimeUpgradeAdvice(string runtime)
    {
        return runtime switch
        {
            "Docker" => $"Upgrade Docker to version {MinimumDockerVersion} or later from: https://www.docker.com/products/docker-desktop",
            "Podman" => $"Upgrade Podman to version {MinimumPodmanVersion} or later from: https://podman.io/getting-started/installation",
            _ => $"Upgrade {runtime} to a newer version"
        };
    }

    private static string GetContainerRuntimeStartupAdvice(string runtime, bool isDockerDesktop = false)
    {
        return runtime switch
        {
            "Docker" when isDockerDesktop => "Start Docker Desktop",
            "Docker" => "Start Docker daemon",
            "Podman" => "Start Podman service: sudo systemctl start podman",
            _ => $"Start {runtime} daemon"
        };
    }
}
