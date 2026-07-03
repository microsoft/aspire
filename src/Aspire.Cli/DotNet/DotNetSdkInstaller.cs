// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Cli.Configuration;
using Microsoft.Extensions.Configuration;
using Semver;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Default implementation of <see cref="IDotNetSdkInstaller"/> that checks for dotnet on the system PATH.
/// </summary>
internal sealed class DotNetSdkInstaller(IFeatures features, IConfiguration configuration) : IDotNetSdkInstaller
{
    /// <summary>
    /// The minimum .NET SDK version required for Aspire.
    /// </summary>
    public const string MinimumSdkVersion = "9.0.302";

    /// <inheritdoc />
    public string DotNetExecutablePath { get; set; } = "dotnet";

    /// <inheritdoc />
    public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        // Check for configuration override first
        var overrideVersion = configuration["overrideMinimumSdkVersion"];
        var minimumVersion = !string.IsNullOrEmpty(overrideVersion) ? overrideVersion : MinimumSdkVersion;

        return CheckAsync(minimumVersion, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CheckAsync(string minimumVersion, CancellationToken cancellationToken = default)
    {
        if (!features.IsFeatureEnabled(KnownFeatures.MinimumSdkCheckEnabled, true))
        {
            // If the feature is disabled, we assume the SDK is available
            return true;
        }

        try
        {
            // Add --arch flag to ensure we only get SDKs that match the current architecture
            var currentArch = GetCurrentArchitecture();
            var arguments = $"--list-sdks --arch {currentArch}";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = DotNetExecutablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                return false;
            }

            // Parse the minimum version requirement
            if (!SemVersion.TryParse(minimumVersion, SemVersionStyles.Strict, out var minVersion))
            {
                return false;
            }

            // Parse each line of the output to find SDK versions
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // Each line is in format: "version [path]"
                var spaceIndex = line.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    var versionString = line[..spaceIndex];
                    if (SemVersion.TryParse(versionString, SemVersionStyles.Strict, out var sdkVersion))
                    {
                        if (SemVersion.ComparePrecedence(sdkVersion, minVersion) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            // If we can't start the process, the SDK is not available
            return false;
        }
    }

    /// <inheritdoc />
    public Task InstallAsync(CancellationToken cancellationToken = default)
    {
        // Reserved for future implementation
        throw new NotImplementedException("SDK installation is not yet implemented.");
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
}