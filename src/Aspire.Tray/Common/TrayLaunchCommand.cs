// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

internal static class TrayLaunchCommand
{
    public static ProcessStartInfo CreateStartInfo(TrayOptions options, string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BundleRoot);
        if (!Path.IsPathFullyQualified(logPath))
        {
            throw new ArgumentException("An absolute log path is required.", nameof(logPath));
        }

        var appPath = Path.Combine(options.BundleRoot, "tray", "Aspire Tray.app");
        var executable = Path.Combine(appPath, "Contents", "MacOS", "aspire-tray");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(appPath, "Contents", "Info.plist")))
        {
            throw new FileNotFoundException("The bundle does not contain the macOS Aspire Tray app.", appPath);
        }

        var startInfo = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in new[]
        {
            "-n", "-g", "--stdout", logPath, "--stderr", logPath, appPath,
            "--args", "--cli", options.CliPath, "--bundle-root", options.BundleRoot
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
