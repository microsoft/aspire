// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Tray;

internal static class TrayLaunchCommand
{
    public static ProcessStartInfo CreateWindowsStartInfo(TrayOptions options)
    {
        if (options.SmokeSeconds is not null || options.BundleRoot is null)
        {
            throw new ArgumentException("Starting the packaged tray requires --bundle-root and does not support smoke mode.");
        }
        if (!Path.IsPathFullyQualified(options.BundleRoot) || !Path.IsPathFullyQualified(options.CliPath))
        {
            throw new ArgumentException("Absolute bundle and CLI paths are required.");
        }

        var executable = Path.Combine(options.BundleRoot, "tray", "aspire-tray.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The bundle does not contain the Windows Aspire Tray executable.", executable);
        }
        if (!File.Exists(options.CliPath))
        {
            throw new FileNotFoundException("The Aspire CLI executable was not found.", options.CliPath);
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in new[] { "--cli", options.CliPath, "--bundle-root", options.BundleRoot })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static string BuildWindowsCommandLine(ProcessStartInfo startInfo)
    {
        var command = new StringBuilder();
        AppendQuoted(startInfo.FileName);
        foreach (var argument in startInfo.ArgumentList)
        {
            command.Append(' ');
            AppendQuoted(argument);
        }

        return command.ToString();

        void AppendQuoted(string argument)
        {
            // CreateProcess consumes a command line, not argv. Match the CRT/.NET rules:
            // a literal quote needs 2n+1 preceding backslashes; a closing quote needs 2n.
            // For example, the argument C:\a b\ becomes "C:\a b\\".
            // https://learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments
            // This is the same algorithm used by the CLI's WindowsProcessInterop.
            command.Append('"');
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                command.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2);
            command.Append('"');
        }
    }

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
