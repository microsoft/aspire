// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if the .NET SDK is installed and meets the minimum version requirement.
/// </summary>
/// <remarks>
/// This check is skipped when the detected AppHost is a non-.NET project (e.g., TypeScript, Python, Go),
/// since .NET SDK is not required for polyglot scenarios.
/// </remarks>
internal sealed class DotNetSdkCheck(
    IDotNetSdkInstaller sdkInstaller,
    IProjectLocator projectLocator,
    ILanguageDiscovery languageDiscovery,
    CliExecutionContext executionContext,
    ILogger<DotNetSdkCheck> logger) : IEnvironmentCheck
{
    public int Order => 30; // File system check - slightly more expensive

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await IsDotNetAppHostAsync(cancellationToken))
            {
                logger.LogDebug("Skipping .NET SDK check because no .NET AppHost was detected");
                return [];
            }

            var (success, highestVersion, minimumRequiredVersion) = await sdkInstaller.CheckAsync(cancellationToken);

            if (!success)
            {
                // Parse major version from string like "10.0.100" -> 10
                var majorVersion = 10;
                if (Version.TryParse(minimumRequiredVersion, out var parsedVersion))
                {
                    majorVersion = parsedVersion.Major;
                }

                return [new EnvironmentCheckResult
                {
                    Category = "sdk",
                    Name = "dotnet-sdk",
                    Status = EnvironmentCheckStatus.Fail,
                    Message = highestVersion is null
                        ? ".NET SDK not found"
                        : $".NET {highestVersion} found but {minimumRequiredVersion} or higher required",
                    Fix = $"Download .NET SDK from: https://dotnet.microsoft.com/download/dotnet/{majorVersion}.0",
                    Link = $"https://dotnet.microsoft.com/download/dotnet/{majorVersion}.0"
                }];
            }

            var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            return [new EnvironmentCheckResult
            {
                Category = "sdk",
                Name = "dotnet-sdk",
                Status = EnvironmentCheckStatus.Pass,
                Message = $".NET {highestVersion} installed ({architecture})"
            }];
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking .NET SDK");
            return [new EnvironmentCheckResult
            {
                Category = "sdk",
                Name = "dotnet-sdk",
                Status = EnvironmentCheckStatus.Fail,
                Message = "Error checking .NET SDK",
                Details = ex.Message
            }];
        }
    }

    /// <summary>
    /// Determines whether a .NET AppHost is positively detected, meaning the .NET SDK check should run.
    /// Only returns <c>true</c> when a settings file is found and the apphost is a .NET project.
    /// When no settings file exists or the apphost is non-.NET, the check is skipped.
    /// </summary>
    private async Task<bool> IsDotNetAppHostAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Use the silent settings-only lookup to find the apphost without
            // emitting interaction output or performing recursive filesystem scans.
            var appHostFile = await projectLocator.GetAppHostFromSettingsAsync(cancellationToken);

            if (appHostFile is not null && languageDiscovery.GetLanguageByFile(appHostFile) is { } language)
            {
                return language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase);
            }

            // No apphost configured in settings — fall back to the same recursive
            // detection that DetectLanguageAsync uses.
            var detectedLanguage = await languageDiscovery.DetectLanguageRecursiveAsync(executionContext.WorkingDirectory, cancellationToken);
            return detectedLanguage is not null &&
                   detectedLanguage.Value.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error detecting AppHost language, skipping .NET SDK check");
            return false;
        }
    }
}
