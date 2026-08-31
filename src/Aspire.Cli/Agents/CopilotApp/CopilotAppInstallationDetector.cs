// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.CopilotApp;

/// <summary>
/// Detects a GitHub Copilot App installation.
/// </summary>
internal interface ICopilotAppInstallationDetector
{
    /// <summary>
    /// Gets the detected installation path, or <see langword="null"/> when the App is not installed.
    /// </summary>
    string? GetInstallationPath();
}

/// <summary>
/// Detects GitHub Copilot App installations from platform installation markers.
/// </summary>
/// <remarks>
/// The supported installers are published at https://github.com/github/app#install.
/// </remarks>
internal sealed class CopilotAppInstallationDetector(
    IEnvironment environment,
    CliExecutionContext executionContext) : ICopilotAppInstallationDetector
{
    internal const string AgentEnvironmentVariable = "AI_AGENT";
    internal const string AgentEnvironmentValue = "github_copilot_app_agent";
    private const string AppDirectoryName = "GitHub Copilot";
    private const string WindowsExecutableName = "github.exe";
    private const string MacOSAppBundleName = "GitHub Copilot.app";

    /// <inheritdoc />
    public string? GetInstallationPath()
    {
        // Unlike Copilot CLI, the App executable is a single-instance GUI application and a
        // `--version` launch does not exit. Use install markers instead; the runtime marker also
        // covers portable/development App builds outside the standard platform directories.
        if (string.Equals(
            environment.GetEnvironmentVariable(AgentEnvironmentVariable),
            AgentEnvironmentValue,
            StringComparison.Ordinal))
        {
            return AgentEnvironmentVariable;
        }

        if (environment.IsWindows())
        {
            var candidatePaths = new List<string>();
            AddWindowsCandidate(candidatePaths, environment.GetEnvironmentVariable("LOCALAPPDATA"), includeProgramsDirectory: true);
            AddWindowsCandidate(
                candidatePaths,
                Path.Combine(executionContext.HomeDirectory.FullName, "AppData", "Local"),
                includeProgramsDirectory: true);
            AddWindowsCandidate(candidatePaths, environment.GetEnvironmentVariable("ProgramFiles"), includeProgramsDirectory: false);
            AddWindowsCandidate(candidatePaths, environment.GetEnvironmentVariable("ProgramFiles(x86)"), includeProgramsDirectory: false);

            return candidatePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(File.Exists);
        }

        if (environment.IsMacOS())
        {
            return new[]
            {
                Path.Combine(Path.DirectorySeparatorChar.ToString(), "Applications", MacOSAppBundleName),
                Path.Combine(executionContext.HomeDirectory.FullName, "Applications", MacOSAppBundleName),
            }
            .FirstOrDefault(Directory.Exists);
        }

        if (environment.IsLinux())
        {
            return FindLinuxDesktopEntry(
                Path.Combine(executionContext.HomeDirectory.FullName, ".local", "share", "applications"))
                ?? FindLinuxDesktopEntry(
                    Path.Combine(Path.DirectorySeparatorChar.ToString(), "usr", "share", "applications"));
        }

        return null;
    }

    private static string? FindLinuxDesktopEntry(string applicationsDirectory)
    {
        if (!Directory.Exists(applicationsDirectory))
        {
            return null;
        }

        string[] desktopFiles;
        try
        {
            desktopFiles = Directory.GetFiles(
                applicationsDirectory,
                "*.desktop",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var desktopFile in desktopFiles)
        {
            try
            {
                // DEB/RPM packages and integrated AppImages register a freedesktop entry shaped as:
                //   [Desktop Entry]
                //   Name=GitHub Copilot
                //   Exec=/path/to/github ...
                // Require both fields so an unrelated text file with the product name is not enough.
                var lines = File.ReadAllLines(desktopFile);
                if (lines.Contains("Name=GitHub Copilot", StringComparer.Ordinal) &&
                    lines.Any(static line =>
                        line.StartsWith("Exec=", StringComparison.Ordinal) &&
                        line.Length > "Exec=".Length))
                {
                    return desktopFile;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable desktop entry cannot prove the App is installed; keep checking
                // other package/AppImage registrations.
            }
        }

        return null;
    }

    private static void AddWindowsCandidate(
        List<string> candidatePaths,
        string? rootDirectory,
        bool includeProgramsDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        candidatePaths.Add(Path.Combine(
            rootDirectory,
            includeProgramsDirectory ? "Programs" : string.Empty,
            AppDirectoryName,
            WindowsExecutableName));
    }
}
