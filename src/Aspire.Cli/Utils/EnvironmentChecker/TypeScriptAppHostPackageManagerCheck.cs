// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Verifies that the package-manager tooling required by the TypeScript AppHost in the
/// current workspace is installed on PATH.
/// </summary>
/// <remarks>
/// The check is gated on TypeScript AppHost discovery: when no <c>apphost.ts</c> is
/// found near the working directory, an empty result list is returned and the
/// <c>polyglot</c> category does not appear in <c>aspire doctor</c> output.
/// </remarks>
internal sealed partial class TypeScriptAppHostPackageManagerCheck(
    CliExecutionContext executionContext,
    ILogger<TypeScriptAppHostPackageManagerCheck> logger) : IEnvironmentCheck
{
    private const string CategoryName = "polyglot";
    private const string DocsLink = "https://aka.ms/aspire-prerequisites";

    private static readonly TimeSpan s_processTimeout = TimeSpan.FromSeconds(10);

    public int Order => 50; // After container (40) and dev-certs (35).

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

        logger.LogDebug(
            "Resolved package manager {PackageManager} for TypeScript AppHost at {Directory} (source: {Source})",
            resolution.PackageManager,
            location.AppHostDirectory.FullName,
            resolution.Source);

        var results = new List<EnvironmentCheckResult>();

        switch (resolution.PackageManager)
        {
            case AppHostPackageManager.Npm:
                results.Add(await CheckCommandAsync(
                    displayName: "npm",
                    executableName: "npm",
                    installLink: GetInstallLink(AppHostPackageManager.Npm),
                    checkName: "npm",
                    cancellationToken));

                results.Add(await CheckNpxAsync(cancellationToken));
                break;

            case AppHostPackageManager.Pnpm:
                results.Add(await CheckCommandAsync(
                    displayName: "pnpm",
                    executableName: "pnpm",
                    installLink: GetInstallLink(AppHostPackageManager.Pnpm),
                    checkName: "pnpm",
                    cancellationToken));
                break;

            case AppHostPackageManager.Bun:
                results.Add(await CheckCommandAsync(
                    displayName: "Bun",
                    executableName: "bun",
                    installLink: GetInstallLink(AppHostPackageManager.Bun),
                    checkName: "bun",
                    cancellationToken));
                break;

            case AppHostPackageManager.YarnBerry:
                results.Add(await CheckYarnAsync(cancellationToken));
                break;

            case AppHostPackageManager.YarnClassic:
                results.Add(BuildYarnClassicWarning());
                break;
        }

        return results;
    }

    private async Task<EnvironmentCheckResult> CheckCommandAsync(
        string displayName,
        string executableName,
        string installLink,
        string checkName,
        CancellationToken cancellationToken)
    {
        var path = PathLookupHelper.FindFullPathFromPath(executableName);
        if (path is null)
        {
            return new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = $"package-manager-{checkName}",
                Status = EnvironmentCheckStatus.Fail,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerMissing, displayName),
                Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerMissingFix, displayName),
                Link = installLink,
            };
        }

        var version = await TryGetVersionAsync(executableName, cancellationToken);

        var message = version is null
            ? string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalledNoVersion, displayName)
            : string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalled, displayName, version);

        return new EnvironmentCheckResult
        {
            Category = CategoryName,
            Name = $"package-manager-{checkName}",
            Status = EnvironmentCheckStatus.Pass,
            Message = message,
            Details = path,
        };
    }

    private async Task<EnvironmentCheckResult> CheckNpxAsync(CancellationToken cancellationToken)
    {
        var path = PathLookupHelper.FindFullPathFromPath("npx");
        if (path is null)
        {
            return new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = "package-manager-npx",
                Status = EnvironmentCheckStatus.Fail,
                Message = DoctorCommandStrings.NpxMissing,
                Fix = DoctorCommandStrings.NpxMissingFix,
                Link = GetInstallLink(AppHostPackageManager.Npm),
            };
        }

        var version = await TryGetVersionAsync("npx", cancellationToken);

        var message = version is null
            ? string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalledNoVersion, "npx")
            : string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalled, "npx", version);

        return new EnvironmentCheckResult
        {
            Category = CategoryName,
            Name = "package-manager-npx",
            Status = EnvironmentCheckStatus.Pass,
            Message = message,
            Details = path,
        };
    }

    private async Task<EnvironmentCheckResult> CheckYarnAsync(CancellationToken cancellationToken)
    {
        var path = PathLookupHelper.FindFullPathFromPath("yarn");
        if (path is null)
        {
            return new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = "package-manager-yarn",
                Status = EnvironmentCheckStatus.Fail,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerMissing, "Yarn"),
                Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerMissingFix, "Yarn"),
                Link = GetInstallLink(AppHostPackageManager.YarnBerry),
            };
        }

        var version = await TryGetVersionAsync("yarn", cancellationToken);

        // If we couldn't determine the version, give the user the benefit of the doubt.
        if (version is null)
        {
            return new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = "package-manager-yarn",
                Status = EnvironmentCheckStatus.Pass,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalledNoVersion, "Yarn"),
                Details = path,
            };
        }

        if (TryParseMajorVersion(version, out var major) && major < 2)
        {
            return new EnvironmentCheckResult
            {
                Category = CategoryName,
                Name = "package-manager-yarn",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.YarnClassicUnsupported,
                Details = string.Format(CultureInfo.CurrentCulture, "yarn --version reported {0} at {1}", version, path),
                Fix = DoctorCommandStrings.YarnClassicUnsupportedFix,
                Link = GetInstallLink(AppHostPackageManager.YarnBerry),
            };
        }

        return new EnvironmentCheckResult
        {
            Category = CategoryName,
            Name = "package-manager-yarn",
            Status = EnvironmentCheckStatus.Pass,
            Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.PackageManagerInstalled, "Yarn", version),
            Details = path,
        };
    }

    private static EnvironmentCheckResult BuildYarnClassicWarning()
    {
        return new EnvironmentCheckResult
        {
            Category = CategoryName,
            Name = "package-manager-yarn",
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.YarnClassicUnsupported,
            Fix = DoctorCommandStrings.YarnClassicUnsupportedFix,
            Link = GetInstallLink(AppHostPackageManager.YarnBerry),
        };
    }

    private async Task<string?> TryGetVersionAsync(string executableName, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executableName,
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

            var trimmed = output.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : NormalizeVersion(trimmed);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to invoke {Executable} --version", executableName);
            return null;
        }
    }

    private static string NormalizeVersion(string value)
    {
        // Some tools emit additional text (e.g., npm prints multiple lines on first run).
        var match = VersionRegex().Match(value);
        return match.Success ? match.Value : value;
    }

    private static bool TryParseMajorVersion(string version, out int major)
    {
        var match = VersionRegex().Match(version);
        if (!match.Success)
        {
            major = 0;
            return false;
        }

        var firstSegment = match.Value.Split('.', 2)[0];
        return int.TryParse(firstSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
    }

    private static string GetInstallLink(AppHostPackageManager packageManager)
    {
        return packageManager switch
        {
            AppHostPackageManager.Npm => "https://nodejs.org/",
            AppHostPackageManager.Pnpm => "https://pnpm.io/installation",
            AppHostPackageManager.Bun => "https://bun.sh/docs/installation",
            AppHostPackageManager.YarnBerry or AppHostPackageManager.YarnClassic => "https://yarnpkg.com/getting-started/install",
            _ => DocsLink,
        };
    }

    [GeneratedRegex(@"\d+(?:\.\d+)*(?:[\-\+][\w\.\-]+)?")]
    private static partial Regex VersionRegex();
}
