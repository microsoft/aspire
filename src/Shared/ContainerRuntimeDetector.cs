// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Shared container runtime detection logic mirroring the approach used by DCP:
//   https://github.com/microsoft/dcp/blob/main/internal/containers/runtimes/runtime.go
//   https://github.com/microsoft/dcp/blob/main/internal/containers/flags/container_runtime.go
//
// Detection strategy (matches DCP's FindAvailableContainerRuntime):
//   1. If a runtime is explicitly configured, use it directly.
//   2. Otherwise, probe all known runtimes in parallel.
//   3. Prefer installed+running over installed-only over not-found.
//   4. When runtimes are equally available, prefer the default (Docker).

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Describes the availability of a single container runtime (e.g., Docker or Podman).
/// </summary>
internal sealed class ContainerRuntimeInfo
{
    /// <summary>
    /// The executable name (e.g., "docker", "podman").
    /// </summary>
    public required string Executable { get; init; }

    /// <summary>
    /// Display name (e.g., "Docker", "Podman").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the runtime CLI was found on PATH.
    /// </summary>
    public bool IsInstalled { get; init; }

    /// <summary>
    /// Whether the runtime daemon/service is responding.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// Whether this is the default runtime when all else is equal.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Error message if detection failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// The client (CLI) version, if detected.
    /// </summary>
    public Version? ClientVersion { get; init; }

    /// <summary>
    /// The server (daemon/engine) version, if detected.
    /// </summary>
    public Version? ServerVersion { get; init; }

    /// <summary>
    /// Whether this is Docker Desktop (vs Docker Engine).
    /// </summary>
    public bool IsDockerDesktop { get; init; }

    /// <summary>
    /// The server OS (e.g., "linux", "windows"). Relevant for Docker's Windows container mode.
    /// </summary>
    public string? ServerOs { get; init; }

    /// <summary>
    /// Whether the runtime is fully operational.
    /// </summary>
    public bool IsHealthy => IsInstalled && IsRunning;
}

/// <summary>
/// Detects available container runtimes by probing CLI executables on PATH.
/// Mirrors the detection logic used by DCP.
/// </summary>
internal static class ContainerRuntimeDetector
{
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromSeconds(10);

    private static readonly (string Executable, string Name, bool IsDefault)[] s_knownRuntimes =
    [
        ("docker", "Docker", true),
        ("podman", "Podman", false)
    ];

    /// <summary>
    /// Finds the best available container runtime, optionally using an explicit preference.
    /// </summary>
    /// <param name="configuredRuntime">
    /// An explicitly configured runtime name (e.g., "docker" or "podman" from ASPIRE_CONTAINER_RUNTIME).
    /// When set, only that runtime is checked. When null, all known runtimes are probed in parallel.
    /// </param>
    /// <param name="logger">Optional logger for diagnostic output during detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The best available runtime, or null if no runtime was found.
    /// When a runtime is configured but not available, returns its info with <see cref="ContainerRuntimeInfo.IsInstalled"/> = false.
    /// </returns>
    public static async Task<ContainerRuntimeInfo?> FindAvailableRuntimeAsync(string? configuredRuntime = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (configuredRuntime is not null)
        {
            // Explicit config: check only the requested runtime
            var known = s_knownRuntimes.FirstOrDefault(r => string.Equals(r.Executable, configuredRuntime, StringComparison.OrdinalIgnoreCase));
            var name = known.Name ?? configuredRuntime;
            var isDefault = known.IsDefault;
            logger?.LogDebug("Checking explicitly configured runtime: {Runtime}", configuredRuntime);
            return await CheckRuntimeAsync(configuredRuntime, name, isDefault, logger, cancellationToken).ConfigureAwait(false);
        }

        // Auto-detect: probe all runtimes in parallel (matches DCP behavior)
        logger?.LogDebug("Auto-detecting container runtime, probing {Count} known runtimes...", s_knownRuntimes.Length);
        var tasks = s_knownRuntimes.Select(r =>
            CheckRuntimeAsync(r.Executable, r.Name, r.IsDefault, logger, cancellationToken)).ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return FindBestRuntime(results);
    }

    /// <summary>
    /// Checks the availability of a specific container runtime.
    /// </summary>
    public static async Task<ContainerRuntimeInfo> CheckRuntimeAsync(string executable, string name, bool isDefault, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            logger?.LogDebug("Probing container runtime '{Name}' ({Executable})...", name, executable);
            // Check if the CLI is installed by running `<runtime> container ls -n 1`
            // This matches DCP's check and also validates the daemon is running.
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "container ls -n 1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ContainerRuntimeInfo
                {
                    Executable = executable,
                    Name = name,
                    IsInstalled = false,
                    IsRunning = false,
                    IsDefault = isDefault,
                    Error = $"{name} CLI not found on PATH."
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(); } catch { /* best effort */ }
                return new ContainerRuntimeInfo
                {
                    Executable = executable,
                    Name = name,
                    IsInstalled = true,
                    IsRunning = false,
                    IsDefault = isDefault,
                    Error = $"{name} CLI timed out while checking status."
                };
            }

            if (process.ExitCode == 0)
            {
                // Runtime is running — gather version metadata
                logger?.LogDebug("{Name} is running, gathering version info...", name);
                var versionInfo = await GetVersionInfoAsync(executable, cancellationToken).ConfigureAwait(false);
                logger?.LogDebug("{Name}: client={ClientVersion}, server={ServerVersion}, desktop={IsDesktop}", name, versionInfo.ClientVersion, versionInfo.ServerVersion, versionInfo.IsDockerDesktop);

                return new ContainerRuntimeInfo
                {
                    Executable = executable,
                    Name = name,
                    IsInstalled = true,
                    IsRunning = true,
                    IsDefault = isDefault,
                    ClientVersion = versionInfo.ClientVersion,
                    ServerVersion = versionInfo.ServerVersion,
                    IsDockerDesktop = versionInfo.IsDockerDesktop,
                    ServerOs = versionInfo.ServerOs
                };
            }

            // Non-zero exit code: CLI exists (we started it) but daemon may not be running.
            var isInstalled = await IsCliInstalledAsync(executable, cancellationToken).ConfigureAwait(false);
            logger?.LogDebug("{Name}: exit code {ExitCode}, installed={IsInstalled}", name, process.ExitCode, isInstalled);

            var partialVersionInfo = isInstalled
                ? await GetVersionInfoAsync(executable, cancellationToken).ConfigureAwait(false)
                : default;

            var error = isInstalled
                ? $"{name} is installed but the daemon is not running."
                : $"{name} CLI not found on PATH.";
            logger?.LogDebug("{Name}: {Error}", name, error);

            return new ContainerRuntimeInfo
            {
                Executable = executable,
                Name = name,
                IsInstalled = isInstalled,
                IsRunning = false,
                IsDefault = isDefault,
                ClientVersion = partialVersionInfo.ClientVersion,
                IsDockerDesktop = partialVersionInfo.IsDockerDesktop,
                Error = error
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            logger?.LogDebug("{Name}: not found on PATH ({ExceptionMessage})", name, ex.Message);
            return new ContainerRuntimeInfo
            {
                Executable = executable,
                Name = name,
                IsInstalled = false,
                IsRunning = false,
                IsDefault = isDefault,
                Error = $"{name} CLI not found on PATH."
            };
        }
    }

    private static async Task<bool> IsCliInstalledAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(); } catch { /* best effort */ }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Selects the best runtime from pre-probed results using DCP's priority logic.
    /// Use this when you've already probed runtimes and want to determine which one to use.
    /// </summary>
    public static ContainerRuntimeInfo? FindBestRuntime(IEnumerable<ContainerRuntimeInfo> results)
    {
        ContainerRuntimeInfo? best = null;
        foreach (var candidate in results)
        {
            if (best is null)
            {
                best = candidate;
                continue;
            }

            if (!best.IsInstalled && candidate.IsInstalled)
            {
                best = candidate;
            }
            else if (!best.IsRunning && candidate.IsRunning)
            {
                best = candidate;
            }
            else if (candidate.IsDefault
                && candidate.IsInstalled == best.IsInstalled
                && candidate.IsRunning == best.IsRunning)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Gathers version metadata from <c>&lt;runtime&gt; version -f json</c>.
    /// </summary>
    private static async Task<RuntimeVersionInfo> GetVersionInfoAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "version -f json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return default;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(); } catch { /* best effort */ }
                return default;
            }

            return ParseVersionOutput(output);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Parses the JSON output from <c>docker/podman version -f json</c> using source-generated JSON serialization.
    /// </summary>
    /// <example>
    /// Docker:
    /// <code>
    /// {"Client":{"Version":"28.0.1","Context":"desktop-linux"},"Server":{"Version":"27.5.0","Os":"linux"}}
    /// </code>
    /// Podman:
    /// <code>
    /// {"Client":{"Version":"4.9.3"},"Server":null}
    /// </code>
    /// </example>
    internal static RuntimeVersionInfo ParseVersionOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return default;
        }

        try
        {
            var json = JsonSerializer.Deserialize(output, ContainerRuntimeJsonContext.Default.ContainerRuntimeVersionJson);
            if (json is null)
            {
                return default;
            }

            Version.TryParse(json.Client?.Version, out var clientVersion);
            Version.TryParse(json.Server?.Version, out var serverVersion);
            var context = json.Client?.Context;
            var isDockerDesktop = context is not null &&
                context.Contains("desktop", StringComparison.OrdinalIgnoreCase);

            return new RuntimeVersionInfo(clientVersion, serverVersion, isDockerDesktop, json.Server?.Os);
        }
        catch (JsonException)
        {
            // Fall back to regex parsing for non-JSON output
            var match = Regex.Match(output, @"[Vv]ersion\s*:?\s*(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
            {
                return new RuntimeVersionInfo(version, null, false, null);
            }

            return default;
        }
    }

    internal readonly record struct RuntimeVersionInfo(
        Version? ClientVersion,
        Version? ServerVersion,
        bool IsDockerDesktop,
        string? ServerOs);
}

internal sealed class ContainerRuntimeVersionJson
{
    [JsonPropertyName("Client")]
    public ContainerRuntimeComponentJson? Client { get; set; }

    [JsonPropertyName("Server")]
    public ContainerRuntimeComponentJson? Server { get; set; }
}

internal sealed class ContainerRuntimeComponentJson
{
    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    [JsonPropertyName("Context")]
    public string? Context { get; set; }

    [JsonPropertyName("Os")]
    public string? Os { get; set; }
}

[JsonSerializable(typeof(ContainerRuntimeVersionJson))]
internal sealed partial class ContainerRuntimeJsonContext : JsonSerializerContext
{
}
