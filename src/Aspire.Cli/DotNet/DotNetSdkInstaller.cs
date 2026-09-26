// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Semver;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Default implementation of <see cref="IDotNetSdkInstaller"/> that checks for dotnet on the system PATH.
/// </summary>
internal sealed class DotNetSdkInstaller(IConfiguration configuration, IEnvironment environment) : IDotNetSdkInstaller
{
    private readonly Func<string, string, ProcessStartInfo> _createProcessStartInfo = CreateProcessStartInfo;

    internal DotNetSdkInstaller(IConfiguration configuration, IEnvironment environment, Func<string, string, ProcessStartInfo> createProcessStartInfo)
        : this(configuration, environment)
    {
        _createProcessStartInfo = createProcessStartInfo;
    }

    /// <summary>
    /// The minimum .NET SDK version required for Aspire.
    /// </summary>
    public const string MinimumSdkVersion = "10.0.100";

    /// <inheritdoc />
    public async Task<(bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion)> CheckAsync(CancellationToken cancellationToken = default)
    {
        var minimumVersion = GetEffectiveMinimumSdkVersion(configuration);

        try
        {
            // Add --arch flag to ensure we only get SDKs that match the current architecture
            var currentArch = GetCurrentArchitecture();
            var arguments = $"--list-sdks --arch {currentArch}";
            var dotnetPath = ResolveDotNetPath(environment);

            using var process = new Process { StartInfo = _createProcessStartInfo(dotnetPath, arguments) };

            process.Start();
            var minVersion = SemVersion.TryParse(minimumVersion, SemVersionStyles.Strict, out var parsedMinimumVersion)
                ? parsedMinimumVersion
                : null;
            SemVersion? highestDetectedVersion = null;
            var meetsMinimum = false;

            try
            {
                await foreach (var line in process.ReadAllLinesAsync(cancellationToken))
                {
                    if (line.StandardError || minVersion is null)
                    {
                        continue;
                    }

                    // SDK records are emitted as "11.0.100 [path]"; diagnostics on stderr
                    // are drained by ReadAllLinesAsync but must not be treated as SDKs.
                    var spaceIndex = line.Content.IndexOf(' ');
                    if (spaceIndex > 0 && SemVersion.TryParse(line.Content[..spaceIndex], SemVersionStyles.Strict, out var sdkVersion))
                    {
                        if (highestDetectedVersion is null || SemVersion.ComparePrecedence(sdkVersion, highestDetectedVersion) > 0)
                        {
                            highestDetectedVersion = sdkVersion;
                        }

                        if (MeetsMinimumRequirement(sdkVersion, minVersion, minimumVersion))
                        {
                            meetsMinimum = true;
                        }
                    }
                }

                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // RunAndCaptureTextAsync only kills the root process on cancellation. The doctor
                // timeout must also stop any descendants, so retain explicit process ownership.
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between cancellation and the kill attempt.
                }

                throw;
            }

            if (process.ExitCode != 0 || minVersion is null)
            {
                return (false, null, minimumVersion);
            }

            return (meetsMinimum, highestDetectedVersion?.ToString(), minimumVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // If cancellation is requested let that bubble up.
        {
            // If we can't start the process, the SDK is not available
            return (false, null, minimumVersion);
        }
    }

    // Use the explicit Windows executable name so lookup still finds dotnet.exe when PATHEXT omits .EXE
    // and does not select an extensionless PATH entry that Process.Start cannot execute on Windows.
    internal static string ResolveDotNetPath(IEnvironment environment) =>
        PathLookupHelper.ResolveExecutablePath(environment.IsWindows() ? "dotnet.exe" : "dotnet");

    private static ProcessStartInfo CreateProcessStartInfo(string dotnetPath, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = dotnetPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    /// <summary>
    /// Gets the current architecture string in the format expected by dotnet --list-sdks --arch.
    /// </summary>
    /// <returns>The architecture string (e.g., "x64", "arm64", "x86", "arm").</returns>
    private static string GetCurrentArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64" // Default to x64 for unknown architectures
        };
    }

    /// <summary>
    /// Gets the effective minimum SDK version based on configuration.
    /// </summary>
    /// <param name="configuration">The configuration to check for overrides.</param>
    /// <returns>The minimum SDK version string.</returns>
    public static string GetEffectiveMinimumSdkVersion(IConfiguration configuration)
    {
        // Check for configuration override first
        var overrideVersion = configuration["overrideMinimumSdkVersion"];

        if (!string.IsNullOrEmpty(overrideVersion))
        {
            return overrideVersion;
        }
        else
        {
            return MinimumSdkVersion;
        }
    }

    /// <summary>
    /// Checks if an installed SDK version meets the minimum requirement.
    /// For .NET 10.x requirements, allows any .NET 10.x version including prereleases.
    /// </summary>
    /// <param name="installedVersion">The installed SDK version.</param>
    /// <param name="requiredVersion">The required minimum version (parsed).</param>
    /// <param name="requiredVersionString">The required version string.</param>
    /// <returns>True if the installed version meets the requirement.</returns>
    private static bool MeetsMinimumRequirement(SemVersion installedVersion, SemVersion requiredVersion, string requiredVersionString)
    {
        // Special handling for .NET 10 RTM requirement - allow any .NET 10.x version
        if (requiredVersionString == MinimumSdkVersion)
        {
            // If we require 10.0.100, accept any version that is >= 10.0.0
            return installedVersion.Major >= 10;
        }

        // For all other requirements, use strict version comparison
        return SemVersion.ComparePrecedence(installedVersion, requiredVersion) >= 0;
    }
}
