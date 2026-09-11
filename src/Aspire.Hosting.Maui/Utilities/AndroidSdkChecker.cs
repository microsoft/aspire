// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui.Annotations;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Checks whether Android SDK tooling required by MAUI Android resources is available.
/// </summary>
internal sealed class AndroidSdkChecker : IMauiPrerequisiteChecker
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> s_dotNetProbeEnvironmentVariables = new Dictionary<string, string>
    {
        [KnownConfigNames.DotnetCliTelemetryOptOut] = "1",
        [KnownConfigNames.DotnetCliWorkloadUpdateNotifyDisable] = "1"
    };

    private readonly Func<IResource, ILogger, CancellationToken, Task<string?>> _getConfiguredSdkPathAsync;
    private readonly Func<string?> _findSdkPath;
    private readonly Func<string, bool> _hasAdbTool;

    public AndroidSdkChecker()
        : this(new ProcessRunner())
    {
    }

    public AndroidSdkChecker(IProcessRunner processRunner)
        : this(processRunner, getConfiguredSdkPathAsync: null, FindAndroidSdkPath, HasAdbTool)
    {
    }

    internal AndroidSdkChecker(Func<string?> findSdkPath)
        : this(new ProcessRunner(), (_, _, _) => Task.FromResult<string?>(null), findSdkPath, _ => true)
    {
    }

    internal AndroidSdkChecker(
        IProcessRunner processRunner,
        Func<IResource, ILogger, CancellationToken, Task<string?>>? getConfiguredSdkPathAsync,
        Func<string?> findSdkPath,
        Func<string, bool> hasAdbTool)
    {
        _getConfiguredSdkPathAsync = getConfiguredSdkPathAsync ?? ((resource, logger, cancellationToken) => GetConfiguredAndroidSdkDirectoryAsync(processRunner, resource, logger, cancellationToken));
        _findSdkPath = findSdkPath;
        _hasAdbTool = hasAdbTool;
    }

    public string Name => "Android SDK";

    public string InstallHint => "Install Android Studio or the Android command-line tools, then set ANDROID_HOME to the SDK path.";

    public string DocumentationUrl => "https://developer.android.com/studio";

    public bool AppliesTo(IResource resource) => resource is MauiAndroidDeviceResource or MauiAndroidEmulatorResource;

    public string GetCacheKey(IResource resource)
    {
        var resourceType = resource.GetType().FullName ?? resource.GetType().Name;
        if (resource.TryGetLastAnnotation<MauiBuildInfoAnnotation>(out var buildInfo))
        {
            return string.Join('\u001f',
                Name,
                resourceType,
                buildInfo.ProjectPath,
                buildInfo.WorkingDirectory,
                buildInfo.TargetFramework ?? string.Empty,
                buildInfo.Configuration ?? string.Empty,
                string.Join('\u001e', buildInfo.AdditionalBuildArguments));
        }

        return resource is IMauiPlatformResource mauiResource
            ? string.Join('\u001f', Name, resourceType, mauiResource.Parent.ProjectPath)
            : string.Join('\u001f', Name, resourceType);
    }

    public async Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        var configuredSdkPath = await _getConfiguredSdkPathAsync(resource, logger, cancellationToken).ConfigureAwait(false);
        var sdkPath = configuredSdkPath ?? _findSdkPath();
        if (sdkPath is null)
        {
            return MauiPrerequisiteCheckResult.Missing("Could not find an Android SDK containing executable `platform-tools/adb`.");
        }

        if (!_hasAdbTool(sdkPath))
        {
            return MauiPrerequisiteCheckResult.Missing(
                $"Android SDK was found at '{sdkPath}', but executable `platform-tools/adb` was not found.");
        }

        logger.LogDebug("Android SDK found at '{SdkPath}'.", sdkPath);
        return MauiPrerequisiteCheckResult.Available;
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

        return HasAdbTool(sdkPath);
    }

    internal static bool HasAdbTool(string sdkPath)
    {
        return FileExistsAndIsExecutable(Path.Combine(sdkPath, "platform-tools", GetExecutableName("adb")));
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

    private static async Task<string?> GetConfiguredAndroidSdkDirectoryAsync(IProcessRunner processRunner, IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        if (!resource.TryGetLastAnnotation<MauiBuildInfoAnnotation>(out var buildInfo))
        {
            return null;
        }

        var args = new List<string> { "msbuild", buildInfo.ProjectPath };
        if (!string.IsNullOrEmpty(buildInfo.TargetFramework))
        {
            args.Add($"-p:{KnownMauiMSBuildProperties.TargetFramework}={buildInfo.TargetFramework}");
        }

        if (!string.IsNullOrEmpty(buildInfo.Configuration))
        {
            args.Add($"-p:Configuration={buildInfo.Configuration}");
        }

        args.AddRange(buildInfo.AdditionalBuildArguments);
        args.Add($"-getProperty:{KnownMauiMSBuildProperties.AndroidSdkDirectory}");
        args.Add("-nologo");

        ProcessResult result;
        try
        {
            // Use PATH-resolved `dotnet` to match project evaluation, the serialized build, and DCP launch.
            result = await processRunner.RunAsync(
                "dotnet",
                args,
                buildInfo.WorkingDirectory,
                s_timeout,
                s_dotNetProbeEnvironmentVariables,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to evaluate {PropertyName} for MAUI project '{ProjectPath}'.", KnownMauiMSBuildProperties.AndroidSdkDirectory, buildInfo.ProjectPath);
            return null;
        }

        if (result.ExitCode != 0)
        {
            logger.LogDebug(
                "Unable to evaluate {PropertyName} for MAUI project '{ProjectPath}'. `dotnet msbuild` exited with code {ExitCode}: {StandardError}",
                KnownMauiMSBuildProperties.AndroidSdkDirectory,
                buildInfo.ProjectPath,
                result.ExitCode,
                result.StandardError.Trim());
            return null;
        }

        return ParseAndroidSdkDirectory(result.StandardOutput);
    }

    internal static string? ParseAndroidSdkDirectory(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // A single-property probe emits the raw value:
        //   /Users/example/Library/Android/sdk
        // Multiple-property probes emit JSON:
        //   { "Properties": { "AndroidSdkDirectory": "/Users/example/Library/Android/sdk" } }
        // Treat any other multiline stdout as non-machine-readable output so first-run or workload
        // notifications cannot be mistaken for part of the SDK path.
        if (trimmed[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.TryGetProperty("Properties", out var properties) &&
                    properties.TryGetProperty(KnownMauiMSBuildProperties.AndroidSdkDirectory, out var androidSdkDirectory))
                {
                    return NormalizeSdkPath(androidSdkDirectory.GetString());
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return null;
        }

        return NormalizeSdkPath(trimmed);
    }

    private static string? NormalizeSdkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool FileExistsAndIsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & ExecuteBits) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
