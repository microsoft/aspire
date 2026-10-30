// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Shared;

/// <summary>
/// Describes the availability of a single container runtime (e.g., Docker, Podman, or WSLc).
/// </summary>
internal sealed class ContainerRuntimeInfo
{
    /// <summary>
    /// The registered runtime key used by configuration and service resolution.
    /// </summary>
    public required string Key { get; init; }

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
    /// Whether the runtime supports the host operating system.
    /// </summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>
    /// Discovery priority when availability is equal. Lower values are preferred.
    /// </summary>
    public int Priority { get; init; } = int.MaxValue;

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

    public bool IsClientPrerelease { get; init; }
    public bool IsServerPrerelease { get; init; }
    public string? VersionError { get; init; }
    public string? ClientVersionText { get; init; }
    public string? ServerVersionText { get; init; }

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
/// Shares availability probing and Docker-compatible version parsing between container runtimes.
/// </summary>
internal class ContainerRuntimeProbe(string key, string executable, string name, int priority)
{
    private static readonly TimeSpan s_processTimeout = TimeSpan.FromSeconds(10);

    public string Key => key;
    public string Executable => executable;
    public string Name => name;
    public int Priority => priority;

    /// <summary>
    /// Determines whether the runtime supports the host operating system.
    /// </summary>
    public virtual bool IsSupportedHost(bool isWindows) => true;

    /// <summary>
    /// Checks CLI installation and runtime health, then gathers available version metadata.
    /// </summary>
    public async Task<ContainerRuntimeInfo> CheckAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupportedHost(OperatingSystem.IsWindows()))
        {
            logger?.LogDebug("{Name} is not supported on this host.", name);
            return new ContainerRuntimeInfo
            {
                Key = key,
                Executable = executable,
                Name = name,
                Priority = priority,
                IsSupported = false
            };
        }

        try
        {
            logger?.LogDebug("Probing container runtime '{Name}' ({Executable})...", name, executable);
            // Check CLI availability and runtime health with `<runtime> container ls -n 1`.
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
                    Key = key,
                    Executable = executable,
                    Name = name,
                    IsInstalled = false,
                    IsRunning = false,
                    Priority = priority,
                    Error = $"{name} CLI not found on PATH."
                };
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                // Consume both pipes while waiting so child writes cannot block process completion.
                await Task.WhenAll(
                    process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeoutCts.Token),
                    errorTask,
                    process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best effort */ }
                cancellationToken.ThrowIfCancellationRequested();

                return new ContainerRuntimeInfo
                {
                    Key = key,
                    Executable = executable,
                    Name = name,
                    IsInstalled = true,
                    IsRunning = false,
                    Priority = priority,
                    Error = $"{name} CLI timed out while checking status."
                };
            }

            if (process.ExitCode == 0)
            {
                // Runtime is running — gather version metadata
                logger?.LogDebug("{Name} is running, gathering version info...", name);
                var versionInfo = await ReadVersionInfoAsync(isRunning: true, logger, cancellationToken).ConfigureAwait(false);
                logger?.LogDebug("{Name}: client={ClientVersion}, server={ServerVersion}, desktop={IsDesktop}", name, versionInfo.ClientVersion, versionInfo.ServerVersion, versionInfo.IsDockerDesktop);

                return new ContainerRuntimeInfo
                {
                    Key = key,
                    Executable = executable,
                    Name = name,
                    IsInstalled = true,
                    IsRunning = true,
                    Priority = priority,
                    ClientVersion = versionInfo.ClientVersion,
                    ServerVersion = versionInfo.ServerVersion,
                    IsClientPrerelease = versionInfo.IsClientPrerelease,
                    IsServerPrerelease = versionInfo.IsServerPrerelease,
                    VersionError = versionInfo.Error,
                    ClientVersionText = versionInfo.ClientVersionText,
                    ServerVersionText = versionInfo.ServerVersionText,
                    IsDockerDesktop = versionInfo.IsDockerDesktop,
                    ServerOs = versionInfo.ServerOs
                };
            }

            // Non-zero exit code: CLI exists (we started it) but daemon may not be running.
            var isInstalled = await IsCliInstalledAsync(logger, cancellationToken).ConfigureAwait(false);
            logger?.LogDebug("{Name}: exit code {ExitCode}, installed={IsInstalled}", name, process.ExitCode, isInstalled);

            var partialVersionInfo = isInstalled
                ? await ReadVersionInfoAsync(isRunning: false, logger, cancellationToken).ConfigureAwait(false)
                : default;

            var error = isInstalled
                ? $"{name} is installed but the daemon is not running."
                : $"{name} CLI not found on PATH.";
            var stderr = (await errorTask.ConfigureAwait(false)).Trim();
            if (stderr.Length > 0)
            {
                error += $"{Environment.NewLine}{stderr}";
            }
            logger?.LogDebug("{Name}: {Error}", name, error);

            return new ContainerRuntimeInfo
            {
                Key = key,
                Executable = executable,
                Name = name,
                IsInstalled = isInstalled,
                IsRunning = false,
                Priority = priority,
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
                Key = key,
                Executable = executable,
                Name = name,
                IsInstalled = false,
                IsRunning = false,
                Priority = priority,
                Error = $"{name} CLI not found on PATH."
            };
        }
    }

    private async Task<bool> IsCliInstalledAsync(ILogger? logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
                var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await Task.WhenAll(
                    process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeoutCts.Token),
                    errorTask,
                    process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    logger?.LogDebug("{Name} installation probe failed with exit code {ExitCode}: {Error}",
                        name, process.ExitCode, await errorTask.ConfigureAwait(false));
                }
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best effort */ }
                cancellationToken.ThrowIfCancellationRequested();

                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "Failed to check {Name} CLI installation.", name);
            return false;
        }
    }

    /// <summary>
    /// Reads the Docker/Podman version protocol, including partial output when the service is unavailable.
    /// </summary>
    protected virtual async Task<RuntimeVersionInfo> ReadVersionInfoAsync(bool isRunning, ILogger? logger, CancellationToken cancellationToken)
    {
        var nonJsonOutput = false;
        var info = await ReadVersionOutputAsync("version -f json", async (stream, token) =>
        {
            try
            {
                var json = await JsonSerializer.DeserializeAsync(stream, ContainerRuntimeJsonContext.Default.ContainerRuntimeVersionJson, token).ConfigureAwait(false);
                return NormalizeVersionInfo(json);
            }
            catch (JsonException ex)
            {
                logger?.LogDebug(ex, "{Name} did not return JSON version metadata.", name);
                nonJsonOutput = true;
                return default;
            }
        }, logger, cancellationToken).ConfigureAwait(false);

        if (nonJsonOutput)
        {
            // Streaming JSON consumes its input before rejecting text. Retrieve the existing
            // text-version fallback separately with `<runtime> --version` (e.g. "podman version 5.0.0").
            return await ReadVersionOutputAsync("--version", async (stream, token) =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                return ParseVersionOutput(await reader.ReadToEndAsync(token).ConfigureAwait(false));
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        return info;
    }

    /// <summary>
    /// Parses streamed metadata while capturing stderr and waiting for process completion.
    /// </summary>
    protected async Task<RuntimeVersionInfo> ReadVersionOutputAsync(
        string arguments,
        Func<Stream, CancellationToken, Task<RuntimeVersionInfo>> parseOutput,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
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

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(s_processTimeout);

                var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                async Task<RuntimeVersionInfo> ParseAndDrainAsync()
                {
                    try
                    {
                        return await parseOutput(process.StandardOutput.BaseStream, timeoutCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // A parser can reject malformed input before EOF; keep draining so the
                        // child can finish writing and stderr collection can complete.
                        await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeoutCts.Token).ConfigureAwait(false);
                    }
                }

                var parseTask = ParseAndDrainAsync();
                try
                {
                    await Task.WhenAll(parseTask, errorTask, process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    logger?.LogDebug("{Name} metadata command timed out.", name);
                    return default;
                }
                catch (JsonException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    logger?.LogDebug(ex, "Invalid {Name} version metadata. Stderr: {Error}", name, await errorTask.ConfigureAwait(false));
                    return default;
                }

                if (process.ExitCode != 0)
                {
                    // Docker can return usable client metadata together with a daemon connection error.
                    logger?.LogDebug("{Name} metadata command failed with exit code {ExitCode}: {Error}",
                        name, process.ExitCode, await errorTask.ConfigureAwait(false));
                }

                return await parseTask.ConfigureAwait(false);
            }
            finally
            {
                // Process.Dispose closes handles but does not terminate a still-running child.
                // Cleanup must not depend on which exception wins when concurrent tasks fail.
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    logger?.LogDebug(ex, "Failed to terminate {Name} metadata process.", name);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "Failed to read {Name} version metadata.", name);
            return default;
        }
    }

    /// <summary>
    /// Parses Docker-compatible version output using source-generated JSON serialization.
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
            return NormalizeVersionInfo(json);
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

    private static RuntimeVersionInfo NormalizeVersionInfo(ContainerRuntimeVersionJson? json)
    {
        var clientVersion = ParseRuntimeVersion(json?.Client?.Version, out var clientPrerelease);
        var serverVersion = ParseRuntimeVersion(json?.Server?.Version, out var serverPrerelease);
        var isDockerDesktop = json?.Client?.Context?.Contains("desktop", StringComparison.OrdinalIgnoreCase) == true;

        return new RuntimeVersionInfo(clientVersion, serverVersion, isDockerDesktop, json?.Server?.Os)
        {
            IsClientPrerelease = clientPrerelease,
            IsServerPrerelease = serverPrerelease,
            ClientVersionText = json?.Client?.Version,
            ServerVersionText = json?.Server?.Version
        };
    }

    internal static Version? ParseRuntimeVersion(string? value, out bool isPrerelease)
    {
        isPrerelease = false;
        if (Version.TryParse(value, out var nativeVersion))
        {
            return nativeVersion;
        }

        // Native WSLc uses four numbers (3.0.3.0). Docker distribution builds and WSLc
        // SemVer releases can instead use 26.1.5+dfsg1 or 3.0.2-rc.1.
        if (value is not null && SemVersion.TryParse(value, SemVersionStyles.Strict, out var semanticVersion) &&
            semanticVersion.Major <= int.MaxValue && semanticVersion.Minor <= int.MaxValue && semanticVersion.Patch <= int.MaxValue)
        {
            isPrerelease = semanticVersion.IsPrerelease;
            return new Version((int)semanticVersion.Major, (int)semanticVersion.Minor, (int)semanticVersion.Patch);
        }

        return null;
    }

    internal readonly record struct RuntimeVersionInfo(
        Version? ClientVersion,
        Version? ServerVersion,
        bool IsDockerDesktop,
        string? ServerOs)
    {
        public bool IsClientPrerelease { get; init; }
        public bool IsServerPrerelease { get; init; }
        public string? Error { get; init; }
        public string? ClientVersionText { get; init; }
        public string? ServerVersionText { get; init; }
    }
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
