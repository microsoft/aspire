// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Checks whether Android SDK tooling required by MAUI Android resources is available.
/// </summary>
internal sealed class AndroidSdkChecker(
    Func<string?> findSdkPath,
    Func<string, bool> hasEmulatorTool) : IMauiPrerequisiteChecker
{
    public AndroidSdkChecker()
        : this(FindAndroidSdkPath, HasEmulatorTool)
    {
    }

    public string Name => "Android SDK";

    public string InstallHint => "Install Android Studio or the Android command-line tools, then set ANDROID_HOME to the SDK path.";

    public string DocumentationUrl => "https://developer.android.com/studio";

    public bool AppliesTo(IResource resource) => resource is MauiAndroidDeviceResource or MauiAndroidEmulatorResource;

    public string GetCacheKey(IResource resource)
    {
        return $"{Name}:{resource.GetType().FullName}";
    }

    public Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        var sdkPath = findSdkPath();
        if (sdkPath is null)
        {
            return Task.FromResult(MauiPrerequisiteCheckResult.Missing("Could not find an Android SDK containing `platform-tools/adb`."));
        }

        if (resource is MauiAndroidEmulatorResource && !hasEmulatorTool(sdkPath))
        {
            return Task.FromResult(MauiPrerequisiteCheckResult.Missing(
                $"Android SDK was found at '{sdkPath}', but the Android emulator tool was not found. Install the Android Emulator package in Android Studio."));
        }

        logger.LogDebug("Android SDK found at '{SdkPath}'.", sdkPath);
        return Task.FromResult(MauiPrerequisiteCheckResult.Available);
    }

    internal static string? FindAndroidSdkPath()
    {
        foreach (var path in GetCandidateSdkPaths())
        {
            if (IsValidSdkPath(path))
            {
                return path;
            }
        }

        var adbPath = PathLookupHelper.FindFullPathFromPath("adb");
        if (adbPath is null)
        {
            return null;
        }

        var platformToolsDir = Path.GetDirectoryName(adbPath);
        if (platformToolsDir is not null &&
            Path.GetFileName(platformToolsDir).Equals("platform-tools", StringComparison.OrdinalIgnoreCase))
        {
            var sdkPath = Path.GetDirectoryName(platformToolsDir);
            if (sdkPath is not null && IsValidSdkPath(sdkPath))
            {
                return sdkPath;
            }
        }

        return null;
    }

    internal static bool IsValidSdkPath(string sdkPath)
    {
        if (!Directory.Exists(sdkPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(sdkPath, "platform-tools", GetExecutableName("adb")));
    }

    internal static bool HasEmulatorTool(string sdkPath)
    {
        return File.Exists(Path.Combine(sdkPath, "emulator", GetExecutableName("emulator"))) ||
            PathLookupHelper.FindFullPathFromPath("emulator") is not null;
    }

    internal static IEnumerable<string> GetCandidateSdkPaths()
    {
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            yield return androidHome;
        }

        var androidSdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidSdkRoot) &&
            !string.Equals(androidSdkRoot, androidHome, StringComparison.Ordinal))
        {
            yield return androidSdkRoot;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Android", "sdk");
            yield return Path.Combine(home, "Library", "Developer", "Xamarin", "android-sdk-macosx");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, "Android", "Sdk");
        }
        else if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Android", "Sdk");
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Android", "android-sdk");
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Android", "android-sdk");
            }
        }
    }

    private static string GetExecutableName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

}
