// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Describes how to reach a launched Android app, as recorded by the generated Android targets file
/// after <c>ComputeRunArguments</c>.
/// </summary>
internal sealed record MauiAndroidLaunchSignal(string AdbPath, IReadOnlyList<string> AdbTargetArguments, string PackageName)
{
    internal const string AdbToolPathKey = "adb-tool-path";
    internal const string AdbToolExeKey = "adb-tool-exe";
    internal const string AdbTargetKey = "adb-target";
    internal const string PackageKey = "package";

    /// <summary>
    /// Gets the MSBuild <c>WriteLinesToFile</c> lines that record the launch signal. The package property
    /// is defined by the generated target from the Android SDK's resolved package name.
    /// </summary>
    internal static string MSBuildLines =>
        $"{AdbToolPathKey}=$(AdbToolPath);{AdbToolExeKey}=$(AdbToolExe);{AdbTargetKey}=$(AdbTarget);{PackageKey}=$(_AspireAndroidPackage)";

    /// <summary>
    /// Gets the adb arguments that stop the app on the device.
    /// </summary>
    public IReadOnlyList<string> GetForceStopArguments() => [.. AdbTargetArguments, "shell", "am", "force-stop", PackageName];

    /// <summary>
    /// Reads a launch signal file, returning <see langword="null"/> when it is missing or does not name a package.
    /// </summary>
    public static MauiAndroidLaunchSignal? TryRead(string filePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        if (!values.TryGetValue(PackageKey, out var packageName) || string.IsNullOrEmpty(packageName))
        {
            return null;
        }

        values.TryGetValue(AdbToolPathKey, out var adbToolPath);
        values.TryGetValue(AdbToolExeKey, out var adbToolExe);
        values.TryGetValue(AdbTargetKey, out var adbTarget);

        if (string.IsNullOrEmpty(adbToolExe))
        {
            adbToolExe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        }

        var adbPath = string.IsNullOrEmpty(adbToolPath) ? adbToolExe : Path.Combine(adbToolPath, adbToolExe);

        // AdbTarget holds adb's device selection switches, for example "-e", "-d", or "-s emulator-5554".
        var adbTargetArguments = string.IsNullOrEmpty(adbTarget)
            ? []
            : adbTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new MauiAndroidLaunchSignal(adbPath, adbTargetArguments, packageName);
    }
}
