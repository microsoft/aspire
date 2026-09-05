// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Checks whether full Xcode is selected for iOS and Mac Catalyst resources.
/// </summary>
internal sealed class XcodeChecker(
    IProcessRunner processRunner,
    Func<bool> isMacOS) : IMauiPrerequisiteChecker
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    public XcodeChecker(IProcessRunner processRunner)
        : this(processRunner, OperatingSystem.IsMacOS)
    {
    }

    public string Name => "Xcode";

    public string InstallHint => "Install Xcode from the Mac App Store, then run `sudo xcode-select -s /Applications/Xcode.app/Contents/Developer` if needed.";

    public string DocumentationUrl => "https://developer.apple.com/xcode/";

    public bool AppliesTo(IResource resource)
    {
        return isMacOS() &&
            resource is MauiiOSSimulatorResource or MauiiOSDeviceResource or MauiMacCatalystPlatformResource;
    }

    public string GetCacheKey(IResource resource)
    {
        return Name;
    }

    public async Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync("xcode-select", ["-p"], workingDirectory: null, s_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MauiPrerequisiteCheckResult.Missing($"Unable to run `xcode-select -p`: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"`xcode-select -p` exited with code {result.ExitCode}."
                : $"`xcode-select -p` exited with code {result.ExitCode}: {result.StandardError.Trim()}";
            return MauiPrerequisiteCheckResult.Missing(details);
        }

        var developerPath = result.StandardOutput.Trim();
        if (IsFullXcodePath(developerPath))
        {
            logger.LogDebug("Full Xcode developer directory found at '{DeveloperPath}'.", developerPath);
            return MauiPrerequisiteCheckResult.Available;
        }

        return MauiPrerequisiteCheckResult.Missing(
            string.IsNullOrWhiteSpace(developerPath)
                ? "`xcode-select -p` did not return a developer directory."
                : $"The selected developer directory '{developerPath}' is not a full Xcode installation.");
    }

    internal static bool IsFullXcodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.Contains("CommandLineTools", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Contains("Xcode", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("Contents/Developer", StringComparison.Ordinal))
        {
            return true;
        }

        return Directory.Exists(Path.Combine(path, "Platforms"));
    }
}
