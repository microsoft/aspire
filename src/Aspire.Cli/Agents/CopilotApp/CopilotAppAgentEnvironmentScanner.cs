// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.CopilotApp;

/// <summary>
/// Scans for GitHub Copilot App installations from platform and runtime markers.
/// </summary>
internal sealed class CopilotAppAgentEnvironmentScanner : IAgentEnvironmentScanner
{
    internal const string AgentEnvironmentVariable = "AI_AGENT";
    internal const string AgentEnvironmentValue = "github_copilot_app_agent";
    private const string AppDirectoryName = "GitHub Copilot";
    private const string WindowsExecutableName = "github.exe";
    private const string MacOSAppBundleName = "GitHub Copilot.app";
    private const string LinuxApplicationsDirectoryName = "applications";

    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly ILogger<CopilotAppAgentEnvironmentScanner> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="CopilotAppAgentEnvironmentScanner"/>.
    /// </summary>
    /// <param name="executionContext">The CLI execution context for locating user installation markers.</param>
    /// <param name="environment">The environment abstraction for reading variables and detecting the host platform.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public CopilotAppAgentEnvironmentScanner(
        CliExecutionContext executionContext,
        IEnvironment environment,
        ILogger<CopilotAppAgentEnvironmentScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);
        _executionContext = executionContext;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task ScanAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (GetInstallationMarker() is { } installationMarker)
        {
            _logger.LogDebug("Detected GitHub Copilot App using installation marker {Marker}", installationMarker);
            context.AddDetectedClient(AgentClientKind.CopilotApp);
        }

        return Task.CompletedTask;
    }

    private string? GetInstallationMarker()
    {
        // Unlike Copilot CLI, the App executable is a single-instance GUI application and a
        // `--version` launch does not exit. Use install markers instead; the runtime marker also
        // covers portable/development App builds outside the standard platform directories.
        if (string.Equals(
            _environment.GetEnvironmentVariable(AgentEnvironmentVariable),
            AgentEnvironmentValue,
            StringComparison.Ordinal))
        {
            return AgentEnvironmentVariable;
        }

        if (_environment.IsWindows())
        {
            var candidatePaths = new List<string>();
            AddWindowsCandidate(candidatePaths, _environment.GetEnvironmentVariable("LOCALAPPDATA"), includeProgramsDirectory: true);
            AddWindowsCandidate(
                candidatePaths,
                Path.Combine(_executionContext.HomeDirectory.FullName, "AppData", "Local"),
                includeProgramsDirectory: true);
            AddWindowsCandidate(candidatePaths, _environment.GetEnvironmentVariable("ProgramFiles"), includeProgramsDirectory: false);
            AddWindowsCandidate(candidatePaths, _environment.GetEnvironmentVariable("ProgramFiles(x86)"), includeProgramsDirectory: false);

            return candidatePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(File.Exists);
        }

        if (_environment.IsMacOS())
        {
            return new[]
            {
                Path.Combine(Path.DirectorySeparatorChar.ToString(), "Applications", MacOSAppBundleName),
                Path.Combine(_executionContext.HomeDirectory.FullName, "Applications", MacOSAppBundleName),
            }
            .FirstOrDefault(Directory.Exists);
        }

        if (_environment.IsLinux())
        {
            foreach (var applicationsDirectory in GetLinuxApplicationDirectories())
            {
                if (FindLinuxDesktopEntry(applicationsDirectory) is { } desktopEntry)
                {
                    return desktopEntry;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the Linux application directories in freedesktop search order.
    /// </summary>
    internal IReadOnlyList<string> GetLinuxApplicationDirectories()
    {
        // Desktop entries are XDG data files, so search the user data root before each system data
        // root. Relative XDG values are invalid and ignored by the freedesktop base-directory spec.
        // https://specifications.freedesktop.org/basedir/latest/
        var applicationsDirectories = new List<string>();
        var configuredDataHome = _environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(configuredDataHome) && Path.IsPathFullyQualified(configuredDataHome)
            ? configuredDataHome
            : Path.Combine(_executionContext.HomeDirectory.FullName, ".local", "share");
        applicationsDirectories.Add(Path.Combine(dataHome, LinuxApplicationsDirectoryName));

        var configuredDataDirectories = _environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(configuredDataDirectories))
        {
            var rootDirectory = Path.DirectorySeparatorChar.ToString();
            applicationsDirectories.Add(Path.Combine(rootDirectory, "usr", "local", "share", LinuxApplicationsDirectoryName));
            applicationsDirectories.Add(Path.Combine(rootDirectory, "usr", "share", LinuxApplicationsDirectoryName));
        }
        else
        {
            foreach (var dataDirectory in configuredDataDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Path.IsPathFullyQualified(dataDirectory))
                {
                    applicationsDirectories.Add(Path.Combine(dataDirectory, LinuxApplicationsDirectoryName));
                }
            }
        }

        return applicationsDirectories.Distinct(StringComparer.Ordinal).ToArray();
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
