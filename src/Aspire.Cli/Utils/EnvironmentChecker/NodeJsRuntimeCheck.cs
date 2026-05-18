// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Verifies that a supported Node.js runtime is available for a TypeScript AppHost.
/// </summary>
/// <remarks>
/// The check is gated on TypeScript AppHost discovery and is skipped when the
/// resolved AppHost package manager is Bun (which ships its own runtime). When
/// Node.js is missing, the check fails; when an older Node version is detected
/// the check emits a warning rather than blocking <c>aspire doctor</c>.
/// </remarks>
internal sealed partial class NodeJsRuntimeCheck(
    CliExecutionContext executionContext,
    ILogger<NodeJsRuntimeCheck> logger) : IEnvironmentCheck
{
    private const string CategoryName = "polyglot";
    private const string DocsLink = "https://nodejs.org/";
    private const string CheckName = "nodejs";

    private static readonly TimeSpan s_processTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Minimum supported Node.js versions, expressed per major line.
    /// </summary>
    private static readonly (int Major, int Minor, int Patch)[] s_minimumSupportedVersions =
    [
        (20, 19, 0),
        (22, 13, 0),
    ];

    public int Order => 45; // Before the package-manager check (50).

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var location = TypeScriptAppHostDiscovery.TryDiscover(executionContext.WorkingDirectory);
        if (location is null)
        {
            return [];
        }

        AppHostPackageManagerResolution resolution;
        try
        {
            resolution = AppHostPackageManagerResolver.Resolve(location.AppHostDirectory);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to resolve package manager for TypeScript AppHost at {Directory}", location.AppHostDirectory.FullName);
            return [];
        }

        // Bun ships its own runtime; we don't require a Node.js install for Bun-managed AppHosts.
        if (resolution.PackageManager == AppHostPackageManager.Bun)
        {
            return [];
        }

        var path = PathLookupHelper.FindFullPathFromPath("node");
        if (path is null)
        {
            return
            [
                new EnvironmentCheckResult
                {
                    Category = CategoryName,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Fail,
                    Message = DoctorCommandStrings.NodeJsMissing,
                    Fix = DoctorCommandStrings.NodeJsMissingFix,
                    Link = DocsLink,
                },
            ];
        }

        var version = await TryGetNodeVersionAsync(cancellationToken);
        if (version is null)
        {
            // Node is on PATH but its version is unknown; treat as a pass with a hint.
            return
            [
                new EnvironmentCheckResult
                {
                    Category = CategoryName,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Pass,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.NodeJsInstalled, "(version unknown)"),
                    Details = path,
                },
            ];
        }

        if (!IsSupportedVersion(version))
        {
            return
            [
                new EnvironmentCheckResult
                {
                    Category = CategoryName,
                    Name = CheckName,
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.NodeJsOutdated, version),
                    Details = path,
                    Fix = DoctorCommandStrings.NodeJsOutdatedFix,
                    Link = DocsLink,
                },
            ];
        }

        return
        [
            new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Pass,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.NodeJsInstalled, version),
                Details = path,
            },
        ];
    }

    private async Task<string?> TryGetNodeVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(s_processTimeout);

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup.
                }
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return ExtractVersion(output);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to invoke node --version");
            return null;
        }
    }

    internal static string? ExtractVersion(string nodeOutput)
    {
        if (string.IsNullOrWhiteSpace(nodeOutput))
        {
            return null;
        }

        var match = NodeVersionRegex().Match(nodeOutput);
        return match.Success ? match.Value : null;
    }

    internal static bool IsSupportedVersion(string version)
    {
        if (!TryParseVersion(version, out var parsed))
        {
            return true;
        }

        // Major versions newer than our known matrix are considered supported.
        var maxKnownMajor = 0;
        foreach (var (major, _, _) in s_minimumSupportedVersions)
        {
            if (major > maxKnownMajor)
            {
                maxKnownMajor = major;
            }
        }

        if (parsed.Major > maxKnownMajor)
        {
            return true;
        }

        foreach (var (major, minor, patch) in s_minimumSupportedVersions)
        {
            if (parsed.Major == major)
            {
                if (parsed.Minor > minor)
                {
                    return true;
                }

                if (parsed.Minor == minor)
                {
                    return parsed.Patch >= patch;
                }

                return false;
            }
        }

        // Major versions older than every known floor are not supported.
        return false;
    }

    private static bool TryParseVersion(string value, out (int Major, int Minor, int Patch) parsed)
    {
        parsed = default;

        var match = NodeVersionRegex().Match(value);
        if (!match.Success)
        {
            return false;
        }

        var parts = match.Value.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        if (parts.Length > 1)
        {
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
        }
        if (parts.Length > 2)
        {
            _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch);
        }

        parsed = (major, minor, patch);
        return true;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex NodeVersionRegex();
}
